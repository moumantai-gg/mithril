# Compose-don't-resolve calibration (mithril#1046) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `AreaCalibration` compose-don't-resolve at runtime: replace the source-precedence-with-residual-gate picker in `MapCalibrationService.GetCalibration` with a residual+ref-count picker; turn the manual calibrate hotkey into a verify-and-warn flow when a converged fit exists; decouple `AutoCalibrationTrigger`'s pre-flight from the picker; delete the #988 monotonicity gate, #1005 regime guard, and `AreaCalibration.LocatorScale` field.

**Architecture:** Three independent code paths share one stored `AreaCalibration` per scene — the texture-canvas consumers (Silmarillion area page, Gwaihir POI authoring) read it for `world → texture-pixel`; the live-overlay consumer composes it with the locator's `texture → screen` per frame; the manual hotkey verifies stored-prediction-vs-detection instead of re-solving. Three legitimate re-solve triggers remain: cold scene (no `AutoCapture`/`UserRefinement` record), user-confirmed recalibrate (armed re-press within 10 s of a drift detection), and the existing wizard `CalibrateCurrentArea` path. Single atomic PR per the #1041 precedent.

**Tech Stack:** .NET 10 / C# latest / xunit + FluentAssertions. `TimeProvider` for the arming-window wall-clock seam (existing `FakeClock : TimeProvider` pattern in `Mithril.Shared.Tests`, `Legolas.Tests`, `ThrottledWarnTests`). `MithrilActivitySources.MapCalibration` `ActivitySource` for the new `calibration.drift_check` span. `IMapState` + `ISceneAssetCache` resolution cascade per #1041.

---

## File map

### Production — new files

| File | Responsibility |
|---|---|
| `src/Mithril.MapCalibration.Capture/DriftCheckOutcome.cs` | Discriminated-union result type for `IAutoCalibrationRunner.CheckDriftAsync`; one record per outcome category (NoStoredCalibration, CaptureFailed, MapNotLocated, NoIconDetections, Inconclusive, Ok, Drift). |
| `src/Mithril.MapCalibration.Capture/ManualCalibrationCoordinator.cs` | DI-singleton owning the manual hotkey's "did the user press to recalibrate, or are we still verifying?" state. Holds `_armedUntil : DateTimeOffset?`, calls `IAutoCalibrationRunner.CheckDriftAsync` / `TryCalibrateCurrentAreaAsync`, routes outcomes through `CalibrationStatusFormatter` to `IOverlayWindow.SetStatusMessage`. |

### Production — modified files

| File | Change |
|---|---|
| `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs` | Replace `GetCalibration` body with residual+ref-count picker; drop `_goodResidualThresholdPx` ctor parameter; add picker logging. |
| `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs` | Drop `_goodResidualThresholdPx` argument from the `MapCalibrationService` construction site. |
| `src/Mithril.Shared/Game/GameConfig.cs` (or wherever `CalibrationGoodResidualPx` lives) | Mark `CalibrationGoodResidualPx` `[Obsolete]` with a "removed in next cycle" note (keeps on-disk JSON round-tripping during the upgrade). |
| `src/Mithril.MapCalibration/AreaCalibration.cs` | Remove `LocatorScale` property (lines ~63–80). No `SchemaVersion` bump. |
| `src/Mithril.MapCalibration.Capture/IAutoCalibrationRunner.cs` | Add `Task<DriftCheckOutcome> CheckDriftAsync(CancellationToken ct)`. |
| `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` | Implement `CheckDriftAsync`; delete `CheckMonotonicAccept`, `IsSameScaleRegime`, `MonotonicResidualRatio`, `MonotonicInlierDelta`, `ScaleRegimeRelTolerance` consts; delete the existing-fit lookup + gate block at lines ~465–482; delete the `LocatorScale = …` stamp at lines ~450–454; add drift-check telemetry span. |
| `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs` | Add new chip messages for DriftCheck outcomes + armed re-press. |
| `src/Mithril.MapCalibration.Capture/Hotkeys/CaptureCalibrateCommand.cs` | Rewire to delegate to `ManualCalibrationCoordinator.HandleHotkeyAsync` instead of calling the engine directly. |
| `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` | Register `ManualCalibrationCoordinator` as singleton; ensure `TimeProvider.System` is available (already registered by the host). |
| `src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs` | Replace `GetCalibration(scene).Source != BundledBaseline` pre-flight with `GetAllSources(scene).Any(s => s.Source is UserRefinement or AutoCapture)`; add per-skip + cold-fire logging. |
| `src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs` | (No code change; `calibration.drift_check` span uses the existing `MapCalibration` source. Doc updates in `docs/perf-trace-schema.md` only.) |
| `docs/perf-trace-schema.md` | Add the `calibration.drift_check` span shape: tags `map.area`, `refs.matched`, `max_residual_px`, `threshold_px`, `outcome`. |

### Tests — new files

| File | Coverage |
|---|---|
| `tests/Mithril.MapCalibration.Tests/MapCalibrationServiceTests.cs` (extend if exists, or create) | Picker rule (§10.1 of spec — 9 cases). |
| `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineDriftCheckTests.cs` | `CheckDriftAsync` outcomes (§10.2 — 8 cases). |
| `tests/Mithril.MapCalibration.Capture.Tests/ManualCalibrationCoordinatorTests.cs` | Hotkey coordinator state machine + chip routing (§10.3 — 7 cases). |

### Tests — modified files

| File | Change |
|---|---|
| `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationTriggerTests.cs` | Add 5 cases for the new `GetAllSources`-backed pre-flight (§10.4). Update any test that asserted the old "Source != BundledBaseline" check. |
| `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` | Delete `*Monotonic*` and `*RegimeGuard*` cases (kept in §10.5 of spec). |

### Tests — deleted files

| File | Reason |
|---|---|
| `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineZoomChangeRegressionTests.cs` | Entire file covered the #1005 regime guard; gate is removed. |

---

## Group A — Picker (`MapCalibrationService.GetCalibration`)

**Code review gate** at the end of Group A: the picker change is self-contained, reviewable, and could in principle be merged on its own.

### Task A1: Picker tests

**Files:**
- Create or modify: `tests/Mithril.MapCalibration.Tests/MapCalibrationServiceTests.cs`

- [ ] **Step 1: Check whether the test project + class exist; create if missing**

Run:
```powershell
Test-Path tests/Mithril.MapCalibration.Tests
```

If false: the project doesn't exist yet — for a new test project, add `tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj` mirroring `tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj` (TFM `net10.0-windows`, ProjectReference to `src/Mithril.MapCalibration/Mithril.MapCalibration.csproj`, xunit + FluentAssertions PackageReferences via central management). Add the new project to `Mithril.slnx`.

- [ ] **Step 2: Write the failing test class**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class MapCalibrationServiceTests
{
    private const string Key = "Map_AreaTest";
    private static readonly MapSceneRef Scene = new("AreaTest", null, Key);

    private static AreaCalibration Cal(double residual, int refs, CalibrationSource source) =>
        new(Scale: 1.0, RotationRadians: 0, OriginX: 0, OriginY: 0,
            ReferenceCount: refs, ResidualPixels: residual) { Source = source };

    private static MapCalibrationService NewSvc(
        IReadOnlyDictionary<string, AreaCalibration>? baseline = null,
        IDictionary<string, AreaCalibration>? userRefs = null) =>
        new(
            baseline: baseline ?? new Dictionary<string, AreaCalibration>(),
            userStore: TestUserRefinementStore.With(userRefs),
            goodResidualThresholdPx: 0, // unused after picker rewrite; will be removed in Task A3
            logger: NullLogger.Instance);

    [Fact]
    public void Picker_HighRefCountBaselineBeatsLowRefUserFit()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.9, 8, CalibrationSource.BundledBaseline) },
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.3, 2, CalibrationSource.UserRefinement) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.BundledBaseline);
    }

    [Fact]
    public void Picker_PrefersLowerResidualAcrossSources()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(2.1, 8, CalibrationSource.BundledBaseline) },
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.6, 5, CalibrationSource.AutoCapture) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.AutoCapture);
    }

    [Fact]
    public void Picker_TiebreaksBySourcePrecedence_UserOverAuto()
    {
        // Both candidates have the same residual + ref count. The user store
        // holds whichever was saved last under one key — we set up a baseline +
        // user-store record both with source-tagged identical numbers.
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration>(),
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.8, 6, CalibrationSource.UserRefinement) });
        // Baseline holds the same numbers tagged AutoCapture to assert source-precedence tiebreak.
        var baseline = new Dictionary<string, AreaCalibration> { [Key] = Cal(0.8, 6, CalibrationSource.AutoCapture) };
        var svc2 = NewSvc(baseline: baseline, userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.8, 6, CalibrationSource.UserRefinement) });
        svc2.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.UserRefinement);
    }

    [Fact]
    public void Picker_TiebreaksBySourcePrecedence_AutoOverBaseline()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.8, 6, CalibrationSource.BundledBaseline) },
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.8, 6, CalibrationSource.AutoCapture) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.AutoCapture);
    }

    [Fact]
    public void Picker_BelowFloorAcrossAll_FallsBackToSourcePrecedence()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.5, 3, CalibrationSource.BundledBaseline) },
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.3, 2, CalibrationSource.UserRefinement) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.UserRefinement);
    }

    [Fact]
    public void Picker_NoCandidates_ReturnsNull()
    {
        NewSvc().GetCalibration(Scene).Should().BeNull();
    }

    [Fact]
    public void Picker_OnlyBaseline_ReturnsBaseline()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(2.1, 6, CalibrationSource.BundledBaseline) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.BundledBaseline);
    }

    [Fact]
    public void Picker_OnlyUserBelowFloor_ReturnsIt()
    {
        var svc = NewSvc(
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.3, 2, CalibrationSource.UserRefinement) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.UserRefinement);
    }
}

internal static class TestUserRefinementStore
{
    public static UserRefinementStore With(IDictionary<string, AreaCalibration>? records)
    {
        // UserRefinementStore is internal; construct via the same in-memory path
        // its existing test usage uses. If a test-only helper doesn't exist yet,
        // add a `UserRefinementStore.ForTests(IDictionary<string, AreaCalibration>)`
        // factory in src/Mithril.MapCalibration/Internal/UserRefinementStore.cs
        // (internal scope; InternalsVisibleTo the test project).
        throw new System.NotImplementedException("Add UserRefinementStore.ForTests(...) factory if missing.");
    }
}
```

- [ ] **Step 3: If `UserRefinementStore` lacks a test-friendly constructor, add one**

Add to `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs`:

```csharp
internal static UserRefinementStore ForTests(IDictionary<string, AreaCalibration>? seed)
{
    // Constructs a UserRefinementStore that does not touch the disk —
    // existing seed dictionary becomes the in-memory state. Used by
    // MapCalibrationServiceTests only; relies on InternalsVisibleTo the test
    // project.
    var store = new UserRefinementStore(/* existing ctor args; pass a no-op path or null per the type's design */);
    if (seed is not null)
    {
        foreach (var kvp in seed) store.Save(kvp.Key, kvp.Value);
    }
    return store;
}
```

Adjust the body to match the existing `UserRefinementStore` ctor signature (likely takes a file path + JSON context + logger — use a tempfile or null-suppress pattern). If `InternalsVisibleTo` for `Mithril.MapCalibration.Tests` is not declared on `Mithril.MapCalibration.csproj`, add it.

- [ ] **Step 4: Run tests; expect them all to FAIL**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter FullyQualifiedName~MapCalibrationServiceTests -v normal`

Expected: 8 tests fail with picker still returning per old source-precedence + residual-threshold rule. Specifically `Picker_PrefersLowerResidualAcrossSources` will return the baseline today; `Picker_HighRefCountBaselineBeatsLowRefUserFit` returns the user fit today.

- [ ] **Step 5: Commit the failing tests**

```bash
git add tests/Mithril.MapCalibration.Tests src/Mithril.MapCalibration/Internal/UserRefinementStore.cs Mithril.slnx
git commit -m "test(map-calibration): failing picker tests for residual+ref-count rule (mithril#1046)"
```

### Task A2: Picker implementation

**Files:**
- Modify: `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`

- [ ] **Step 1: Replace the `GetCalibration` body**

```csharp
public AreaCalibration? GetCalibration(MapSceneRef scene)
{
    if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return null;

    var candidates = new List<AreaCalibration>(capacity: 2);
    if (_userStore.TryGet(scene.MapAssetKey, out var user)) candidates.Add(user);
    if (_baseline.TryGetValue(scene.MapAssetKey, out var baseline)) candidates.Add(baseline);
    // CommunitySync slot reserved.

    if (candidates.Count == 0) return null;

    var eligible = candidates.Where(c => c.ReferenceCount >= MinReferences).ToList();
    AreaCalibration picked;
    if (eligible.Count == 0)
    {
        picked = candidates.OrderByDescending(SourceRank).First();
        _logger?.LogInformation(
            "GetCalibration({MapAssetKey}): no candidate cleared MinReferences={Floor}; returning best-source-precedence fallback (source={Source}, residual={Residual:0.00}px, refs={Refs}).",
            scene.MapAssetKey, MinReferences, picked.Source, picked.ResidualPixels, picked.ReferenceCount);
    }
    else
    {
        picked = eligible.OrderBy(c => c.ResidualPixels).ThenByDescending(SourceRank).First();
        _logger?.LogTrace(
            "GetCalibration({MapAssetKey}): {Eligible}/{Total} eligible, picked source={Source} residual={Residual:0.00}px refs={Refs}.",
            scene.MapAssetKey, eligible.Count, candidates.Count, picked.Source, picked.ResidualPixels, picked.ReferenceCount);
    }
    return picked;
}

internal const int MinReferences = 4;

private static int SourceRank(AreaCalibration c) => c.Source switch
{
    CalibrationSource.UserRefinement  => 4,
    CalibrationSource.AutoCapture     => 3,
    CalibrationSource.CommunitySync   => 2,
    CalibrationSource.BundledBaseline => 1,
    _ => 0,
};
```

- [ ] **Step 2: Run picker tests; expect PASS**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter FullyQualifiedName~MapCalibrationServiceTests -v normal`

Expected: 8 picker tests pass.

- [ ] **Step 3: Run the full Mithril.MapCalibration test suite to confirm no regression**

Run: `dotnet test tests/Mithril.MapCalibration.Tests`

Expected: all pass.

- [ ] **Step 4: Commit the picker implementation**

```bash
git add src/Mithril.MapCalibration/Internal/MapCalibrationService.cs
git commit -m "feat(map-calibration): residual+ref-count picker in GetCalibration (mithril#1046)"
```

### Task A3: Drop `_goodResidualThresholdPx` ctor parameter

**Files:**
- Modify: `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`
- Modify: `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`
- Modify: `src/Mithril.Shared/Game/GameConfig.cs` (or wherever `CalibrationGoodResidualPx` is declared — `grep -rn 'CalibrationGoodResidualPx' src/` to locate)

- [ ] **Step 1: Remove the field, ctor param, and `_goodResidualThresholdPx` reference from `MapCalibrationService`**

Delete:
```csharp
private readonly double _goodResidualThresholdPx;
```

Remove the `goodResidualThresholdPx` parameter from the ctor; remove the assignment in the body.

- [ ] **Step 2: Update the DI extension to stop passing the threshold**

In `MapCalibrationServiceCollectionExtensions.cs`, find the `new MapCalibrationService(...)` call site; drop the threshold argument. Remove any code that reads `CalibrationGoodResidualPx` from `GameConfig` for this purpose. Other consumers (if any) of `CalibrationGoodResidualPx` keep working — but mark the property `[Obsolete]`.

- [ ] **Step 3: Mark `GameConfig.CalibrationGoodResidualPx` `[Obsolete]`**

```csharp
[Obsolete("Removed by mithril#1046 — the picker uses ReferenceCount + ResidualPixels ordering instead of a threshold. Field stays one cycle so on-disk JSON round-trips; remove in the next release cycle.")]
public double CalibrationGoodResidualPx { get; init; } = 1.5;
```

- [ ] **Step 4: Build the solution**

Run: `dotnet build Mithril.slnx`

Expected: success. Any warning about the obsolete property at non-test call sites should be silenced via `#pragma warning disable CS0618 // Obsolete` on the JSON round-trip site only. If a call site actively reads `CalibrationGoodResidualPx`, decide per-site: if it's only loading the JSON value, suppress; if it's branching on the value, audit (the property is no longer driving behavior).

- [ ] **Step 5: Run full test suite**

Run: `dotnet test Mithril.slnx`

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration src/Mithril.Shared/Game/GameConfig.cs
git commit -m "refactor(map-calibration): drop goodResidualThresholdPx ctor param; mark GameConfig property [Obsolete] (mithril#1046)"
```

### Task A4: Picker logging integration test

**Files:**
- Modify: `tests/Mithril.MapCalibration.Tests/MapCalibrationServiceTests.cs`

- [ ] **Step 1: Add a logging test that uses a capturing `ILogger`**

```csharp
private sealed class CapturingLogger : ILogger
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
    private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() {} }
}

[Fact]
public void Picker_LogsTraceOnPickAndInfoOnFallback()
{
    var logger = new CapturingLogger();
    var svc = new MapCalibrationService(
        baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(2.1, 6, CalibrationSource.BundledBaseline) },
        userStore: TestUserRefinementStore.With(new Dictionary<string, AreaCalibration> { [Key] = Cal(0.6, 5, CalibrationSource.AutoCapture) }),
        goodResidualThresholdPx: 0, // unused after A2's body rewrite; ctor param itself goes away in A3
        logger: logger);

    svc.GetCalibration(Scene);
    logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Trace && e.Message.Contains("picked source=AutoCapture"));

    var fallbackSvc = new MapCalibrationService(
        baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.5, 3, CalibrationSource.BundledBaseline) },
        userStore: TestUserRefinementStore.With(new Dictionary<string, AreaCalibration> { [Key] = Cal(0.3, 2, CalibrationSource.UserRefinement) }),
        goodResidualThresholdPx: 0, // unused; ctor param itself goes away in A3
        logger: logger);

    fallbackSvc.GetCalibration(Scene);
    logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("best-source-precedence fallback"));
}
```

Note: this test depends on the threshold ctor param removed in A3 — if reordered, update the ctor call. After A3, drop the `goodResidualThresholdPx: 0` argument.

- [ ] **Step 2: Run; expect PASS**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "Picker_LogsTraceOnPickAndInfoOnFallback"`

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.MapCalibration.Tests/MapCalibrationServiceTests.cs
git commit -m "test(map-calibration): picker logging coverage (mithril#1046)"
```

---

### 🟢 Code review gate A — picker change

**Stop. Request review before proceeding to Group B.**

Diff scope: `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`, `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`, `src/Mithril.Shared/Game/GameConfig.cs`, `tests/Mithril.MapCalibration.Tests/*`. Use `superpowers:requesting-code-review`.

What the reviewer should check:
- Picker rule matches §3 D3 and §5.1 of the spec (`MinReferences = 4`, source-precedence tiebreak, fallback when no candidate clears the floor).
- Picker doesn't break existing wiring: `MapCalibrationService` ctor change propagated to every call site; obsolete `CalibrationGoodResidualPx` property doesn't trip warnings-as-errors at any consumer.
- Picker logging cadence is sane (Trace per call, Information only on fallback).

Do not start Group B until the reviewer signs off.

---

## Group B — DriftCheck engine (`AutoCalibrationEngine.CheckDriftAsync`)

### Task B1: `DriftCheckOutcome` discriminated union

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/DriftCheckOutcome.cs`

- [ ] **Step 1: Write the type**

```csharp
namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Outcome of one <see cref="IAutoCalibrationRunner.CheckDriftAsync"/> attempt
/// (mithril#1046 §6.1). The manual hotkey coordinator branches on the concrete
/// case to decide whether to arm, surface a chip, or fall through to a cold
/// solve.
/// </summary>
public abstract record DriftCheckOutcome
{
    /// <summary>No stored calibration exists for the current scene — caller
    /// should fall through to the cold solve path.</summary>
    public sealed record NoStoredCalibration : DriftCheckOutcome;

    /// <summary>Map capture failed (black frame, wrong size, PG not foreground).
    /// Surface <paramref name="Reason"/> via the chip; do not arm.</summary>
    public sealed record CaptureFailed(string Reason) : DriftCheckOutcome;

    /// <summary>The locator couldn't find the map sub-rect in the captured
    /// frame. Surface <paramref name="Reason"/> via the chip; do not arm.</summary>
    public sealed record MapNotLocated(string Reason) : DriftCheckOutcome;

    /// <summary>The typed icon detector found nothing in the captured frame —
    /// can't compare predictions to detections. Do not arm.</summary>
    public sealed record NoIconDetections : DriftCheckOutcome;

    /// <summary>Fewer than the minimum matched references survived the 20-px
    /// gate — drift is not measurable. Do not arm.</summary>
    public sealed record Inconclusive(string Reason, int MatchedReferences) : DriftCheckOutcome;

    /// <summary>Predictions land on detections within the drift tolerance —
    /// the stored calibration is fine; no recalibration needed.</summary>
    public sealed record Ok(double MaxResidualPx, int MatchedReferences) : DriftCheckOutcome;

    /// <summary>At least one matched reference exceeds the drift tolerance.
    /// Coordinator should arm the hotkey for a confirmation re-press.</summary>
    public sealed record Drift(double MaxResidualPx, int MatchedReferences, double ThresholdPx) : DriftCheckOutcome;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Mithril.MapCalibration.Capture`

Expected: success.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/DriftCheckOutcome.cs
git commit -m "feat(map-calibration): DriftCheckOutcome discriminated union (mithril#1046)"
```

### Task B2: `IAutoCalibrationRunner.CheckDriftAsync` interface addition

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/IAutoCalibrationRunner.cs`

- [ ] **Step 1: Add the new method to the interface**

```csharp
/// <summary>
/// Verify the stored calibration against fresh locator + icon-detector output
/// (mithril#1046 §6). Returns a <see cref="DriftCheckOutcome"/> the
/// <see cref="ManualCalibrationCoordinator"/> branches on to decide
/// (a) chip-only no-op, (b) arm-and-warn, or (c) fall-through to a cold
/// solve. Never persists.
/// </summary>
Task<DriftCheckOutcome> CheckDriftAsync(CancellationToken ct);
```

- [ ] **Step 2: Build — expect a compile error in `AutoCalibrationEngine` (does not yet implement the new method)**

Run: `dotnet build src/Mithril.MapCalibration.Capture`

Expected: CS0535 "does not implement interface member CheckDriftAsync".

- [ ] **Step 3: Add a temporary stub in `AutoCalibrationEngine` to unblock the build**

```csharp
public Task<DriftCheckOutcome> CheckDriftAsync(CancellationToken ct) =>
    throw new NotImplementedException("Implemented in Task B4.");
```

- [ ] **Step 4: Build**

Run: `dotnet build`

Expected: success.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/IAutoCalibrationRunner.cs src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs
git commit -m "feat(map-calibration): IAutoCalibrationRunner.CheckDriftAsync surface (mithril#1046)"
```

### Task B3: Failing DriftCheck tests

**Files:**
- Create: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineDriftCheckTests.cs`

- [ ] **Step 1: Examine the existing engine-test fixtures**

Read `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs` and `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` to identify the existing test helpers: `FakeMapState`, `FakeSceneAssetCache`, `SpyCapture`, etc. The drift-check tests reuse these.

- [ ] **Step 2: Write the test class with all 8 cases from spec §10.2**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.MapCalibration;
using System.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class AutoCalibrationEngineDriftCheckTests
{
    private const string Asset = "Map_AreaTest";
    private static readonly MapSceneRef Scene = new("AreaTest", null, Asset);

    private static AreaCalibration Stored(double residual = 0.7) =>
        new(Scale: 1.0, RotationRadians: 0, OriginX: 100, OriginY: 100,
            ReferenceCount: 6, ResidualPixels: residual)
        { Source = CalibrationSource.AutoCapture };

    [Fact]
    public async Task DriftCheck_NoStoredCalibration_ReturnsNoStoredCalibration()
    {
        var engine = NewEngine(cal: null);
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        outcome.Should().BeOfType<DriftCheckOutcome.NoStoredCalibration>();
    }

    [Fact]
    public async Task DriftCheck_PredictedMatchesDetections_ReturnsOk()
    {
        var engine = NewEngine(
            cal: Stored(),
            detector: FakeDetector.AtPredictedPositions(offsetPx: 0.5),
            references: TestReferences.Six);
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        outcome.Should().BeOfType<DriftCheckOutcome.Ok>()
            .Which.MatchedReferences.Should().Be(6);
        ((DriftCheckOutcome.Ok)outcome).MaxResidualPx.Should().BeLessThan(1.0);
    }

    [Fact]
    public async Task DriftCheck_PredictedMissesDetections_ReturnsDrift()
    {
        var engine = NewEngine(
            cal: Stored(residual: 0.7),
            detector: FakeDetector.AtPredictedPositions(offsetPx: 5.0),
            references: TestReferences.Six);
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        var drift = outcome.Should().BeOfType<DriftCheckOutcome.Drift>().Subject;
        drift.MaxResidualPx.Should().BeGreaterThan(2.1); // 3.0 × 0.7
        drift.ThresholdPx.Should().BeApproximately(2.1, 0.01);
    }

    [Fact]
    public async Task DriftCheck_FewerThan3Matched_ReturnsInconclusive()
    {
        var engine = NewEngine(
            cal: Stored(),
            detector: FakeDetector.WithDetectionsAtFirstNPredictions(2, offsetPx: 0.5),
            references: TestReferences.Six);
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        outcome.Should().BeOfType<DriftCheckOutcome.Inconclusive>()
            .Which.MatchedReferences.Should().Be(2);
    }

    [Fact]
    public async Task DriftCheck_LocatorFails_ReturnsMapNotLocated()
    {
        var engine = NewEngine(
            cal: Stored(),
            refiner: FakeRefiner.RejectMap("low inlier count"));
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        outcome.Should().BeOfType<DriftCheckOutcome.MapNotLocated>()
            .Which.Reason.Should().Contain("low inlier count");
    }

    [Fact]
    public async Task DriftCheck_CaptureFails_ReturnsCaptureFailed()
    {
        var engine = NewEngine(
            cal: Stored(),
            capture: FakeCapture.ReturnNullGray());
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        outcome.Should().BeOfType<DriftCheckOutcome.CaptureFailed>();
    }

    [Fact]
    public async Task DriftCheck_LogsExpectedSequence()
    {
        var logger = new CapturingLogger();
        var engine = NewEngine(
            cal: Stored(),
            detector: FakeDetector.AtPredictedPositions(offsetPx: 0.5),
            references: TestReferences.Six,
            logger: logger);
        await engine.CheckDriftAsync(CancellationToken.None);
        logger.Entries.Should().Contain(e => e.Message.Contains("Drift check starting"));
        logger.Entries.Should().Contain(e => e.Message.Contains("locator scale="));
        logger.Entries.Should().Contain(e => e.Message.Contains("Drift check") && e.Message.Contains("OK"));
    }

    [Fact]
    public async Task DriftCheck_EmitsCalibrationDriftCheckSpan()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Mithril.MapCalibration",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var engine = NewEngine(
            cal: Stored(),
            detector: FakeDetector.AtPredictedPositions(offsetPx: 0.5),
            references: TestReferences.Six);
        await engine.CheckDriftAsync(CancellationToken.None);

        var span = spans.Should().ContainSingle(s => s.OperationName == "calibration.drift_check").Subject;
        span.GetTagItem("outcome").Should().Be("Ok");
        span.GetTagItem("refs.matched").Should().Be(6);
    }

    // ---- helpers below ----
    private static AutoCalibrationEngine NewEngine(
        AreaCalibration? cal,
        IMapRegionRefiner? refiner = null,
        ICaptureService? capture = null,
        FakeDetector? detector = null,
        IReadOnlyList<CalibrationReference>? references = null,
        CapturingLogger? logger = null)
    {
        // Wire up the engine with fakes. The detector seam is new — until
        // CheckDriftAsync is implemented (Task B4), this method may need to
        // pass the detector via a new IIconDetector injection or via an
        // engine-side hook the production solver uses. See implementation
        // notes in Task B4: the existing IMapCalibrationSolver.Solve invocation
        // already runs detections internally; the drift-check path needs the
        // raw TypedDetection list, not the solver's geometric fit.
        //
        // Concrete wiring: add an internal seam IIconDetector to the engine
        // ctor (default to a production adapter; tests inject FakeDetector).
        throw new NotImplementedException("Implemented as part of Task B4 wiring.");
    }
}

internal sealed class FakeDetector
{
    public static FakeDetector AtPredictedPositions(double offsetPx) => throw new NotImplementedException();
    public static FakeDetector WithDetectionsAtFirstNPredictions(int n, double offsetPx) => throw new NotImplementedException();
}

internal static class FakeRefiner
{
    public static IMapRegionRefiner RejectMap(string reason) => throw new NotImplementedException();
    public static IMapRegionRefiner AcceptWithMetrics(LocateMetrics m) => throw new NotImplementedException();
}

internal static class FakeCapture
{
    public static ICaptureService ReturnNullGray() => throw new NotImplementedException();
}

internal static class TestReferences
{
    public static IReadOnlyList<CalibrationReference> Six => throw new NotImplementedException();
}

internal sealed class CapturingLogger : ILogger
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
    private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() {} }
}
```

The helper stubs throw `NotImplementedException` — they're filled in during Task B4 when the implementation reveals the exact seams needed. This is intentional per TDD: the tests describe behavior, the impl drives the helper API.

- [ ] **Step 3: Build — expect compile success but tests fail at runtime**

Run: `dotnet build tests/Mithril.MapCalibration.Capture.Tests`

Expected: success (helpers compile because their bodies throw — they're declared).

- [ ] **Step 4: Run tests; expect all 8 to FAIL with NotImplementedException**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter FullyQualifiedName~AutoCalibrationEngineDriftCheckTests`

Expected: 8 tests fail.

- [ ] **Step 5: Commit failing tests + helper stubs**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineDriftCheckTests.cs
git commit -m "test(map-calibration): failing drift-check tests for mithril#1046 §6.1 outcomes"
```

### Task B4: Implement `CheckDriftAsync`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineDriftCheckTests.cs` (replace `NotImplementedException` stubs in `FakeDetector`, `FakeRefiner`, `FakeCapture`, `TestReferences` with concrete bodies driven by the implementation)

- [ ] **Step 1: Add drift-check consts to `AutoCalibrationEngine`**

```csharp
private const double DriftToleranceFactor = 3.0;
private const double DriftMatchGatePx = 20.0;
private const int DriftMinMatchedReferences = 3;
```

- [ ] **Step 2: Identify the icon-detector seam**

Read the existing `IMapCalibrationSolver.Solve(DetectionRequest, references)` call in `RunAttemptCoreAsync`. The solver internally runs typed detection (`TypeAwareRansacSolver`) and returns `CalibrationSolveResult` with a `Detections` list. The drift-check path needs the *raw* typed-detection list before the geometric solve.

Two implementation options:
- **Option A**: factor out the detection call to a separate method on the solver (or a new `IIconDetector` seam) and call it from both `RunAttemptCoreAsync` and `CheckDriftAsync`.
- **Option B**: invoke `_solver.Solve` from `CheckDriftAsync` too and read its `Detections` property — the solver still does the geometric fit work, which is wasted, but no new seam is needed.

Pick **A** — `CheckDriftAsync` runs on every manual hotkey press and shouldn't pay for the solver fit it discards. The new seam is internal to the engine + tests. Add a private method:

```csharp
private TypedDetectionResult RunTypedDetection(DetectionRequest request)
{
    // Existing solver internals invoke this; refactor out so CheckDriftAsync
    // can call the same path without paying for the geometric solve.
    return _detector.Detect(request);
}
```

If a clean factoring is non-trivial, instead expose `IMapCalibrationSolver.DetectOnly(DetectionRequest)` and have `Solve` delegate to it. The plan-side decision is "no new IIconDetector seam in this PR; the existing solver gets a detect-only entry point."

- [ ] **Step 3: Implement `CheckDriftAsync` per §6.2**

```csharp
public async Task<DriftCheckOutcome> CheckDriftAsync(CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    using var span = MithrilActivitySources.MapCalibration.StartActivity("calibration.drift_check");

    var resolvedScene = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache);
    if (resolvedScene is not { } sceneRef || string.IsNullOrWhiteSpace(sceneRef.ParentAreaKey))
    {
        return new DriftCheckOutcome.NoStoredCalibration();
    }

    var stored = _calibrationService.GetCalibration(sceneRef);
    if (stored is null)
    {
        span?.SetTag("outcome", "NoStoredCalibration");
        return new DriftCheckOutcome.NoStoredCalibration();
    }

    var references = _references.ForArea(sceneRef);
    _logger?.LogInformation(
        "Drift check starting for {MapAssetKey}: {Refs} references, tolerance factor {Factor}× of stored {Residual:0.00}px.",
        sceneRef.MapAssetKey, references.Count, DriftToleranceFactor, stored.ResidualPixels);

    if (_windowLocator.Locate() is null)
    {
        span?.SetTag("outcome", "CaptureFailed");
        return new DriftCheckOutcome.CaptureFailed("Project Gorgon is not the foreground window");
    }
    var bbox = _region.Current;
    if (bbox is null)
    {
        span?.SetTag("outcome", "CaptureFailed");
        return new DriftCheckOutcome.CaptureFailed("no map bbox set — use the draw-map-bbox hotkey first");
    }

    var captureResult = await _capture.CaptureMapAsync(bbox.Value, ct).ConfigureAwait(false);
    if (captureResult.Gray is null)
    {
        span?.SetTag("outcome", "CaptureFailed");
        return new DriftCheckOutcome.CaptureFailed("map capture failed (black/wrong-size frame)");
    }

    var baseTexture = await ResolveBaseTextureAsync(sceneRef.MapAssetKey, ct).ConfigureAwait(false);
    if (baseTexture is null)
    {
        span?.SetTag("outcome", "MapNotLocated");
        return new DriftCheckOutcome.MapNotLocated("base texture unavailable");
    }

    var refineResult = _refiner.Refine(captureResult.Gray, baseTexture);
    if (refineResult.AcceptedRect is null || refineResult.Metrics is null)
    {
        var reason = refineResult.Metrics is { } m
            ? $"locator inliers={m.InlierCount}/{m.CandidateCount} scale={m.Scale:0.00}"
            : "no fit";
        _logger?.LogInformation("Drift check {MapAssetKey}: locator failed ({Reason}).", sceneRef.MapAssetKey, reason);
        span?.SetTag("outcome", "MapNotLocated");
        return new DriftCheckOutcome.MapNotLocated(reason);
    }
    var loc = refineResult.Metrics;
    _logger?.LogInformation(
        "Drift check {MapAssetKey}: locator scale={Scale:0.000}, rotation={Rot:0.00}°, inliers={Inliers}/{Cand}, locator residual={LocResid:0.00}px.",
        sceneRef.MapAssetKey, loc.Scale, loc.RotationDegrees, loc.InlierCount, loc.CandidateCount, loc.ResidualPixels);

    // Resolve typed detections in the captured frame (no geometric solve).
    var templates = await EnsureIconTemplatesAsync(ct).ConfigureAwait(false);
    var clamped = ClampToFrame(refineResult.AcceptedRect, captureResult.Gray.Width, captureResult.Gray.Height)!;
    var crop = ImageOps.Crop(captureResult.Gray, clamped.OriginX, clamped.OriginY, clamped.Width, clamped.Height);
    var alignedTexture = ImageOps.Resize(baseTexture, clamped.Width, clamped.Height);
    var alignedRect = new MapRect(0, 0, clamped.Width, clamped.Height, clamped.TextureWidth, clamped.TextureHeight);
    var detectionRequest = new DetectionRequest(
        Screenshot: crop, BaseTexture: alignedTexture, MapRect: alignedRect, Templates: templates,
        RimMask: RimMaskMode.DeviationFlood, LowNcc: LowNcc, TypeFloor: TypeFloor, BlobOptions: BlobOpts)
        { RenderSizePx = RenderSizePx };
    var detections = _solver.DetectOnly(detectionRequest);
    if (detections.Count == 0)
    {
        span?.SetTag("outcome", "NoIconDetections");
        return new DriftCheckOutcome.NoIconDetections();
    }

    // Project each reference, find nearest detection within DriftMatchGatePx.
    var residuals = new List<(string Name, double Px, double Py, double Dx, double Dy, double Dist)>();
    foreach (var r in references)
    {
        var predictedTex = stored.WorldToWindow(r.World, currentZoom: 1.0);
        var predScreenX = predictedTex.X * loc.Scale + loc.Tx;
        var predScreenY = predictedTex.Y * loc.Scale + loc.Ty;
        var nearest = detections
            .Select(d => (d, dist: Math.Sqrt(Math.Pow(d.X - predScreenX, 2) + Math.Pow(d.Y - predScreenY, 2))))
            .OrderBy(t => t.dist)
            .FirstOrDefault();
        if (nearest.d is null || nearest.dist > DriftMatchGatePx) continue;
        residuals.Add((r.Name, predScreenX, predScreenY, nearest.d.X, nearest.d.Y, nearest.dist));
        _logger?.LogTrace(
            "Drift check {MapAssetKey}: ref '{Name}' predicted=({Px:0.0},{Py:0.0}), nearest detection=({Dx:0.0},{Dy:0.0}) at {Dist:0.00}px.",
            sceneRef.MapAssetKey, r.Name, predScreenX, predScreenY, nearest.d.X, nearest.d.Y, nearest.dist);
    }

    if (residuals.Count < DriftMinMatchedReferences)
    {
        _logger?.LogInformation(
            "Drift check {MapAssetKey}: inconclusive — too few visible landmarks ({Matched} matched, need ≥{Min}). No arming.",
            sceneRef.MapAssetKey, residuals.Count, DriftMinMatchedReferences);
        span?.SetTag("outcome", "Inconclusive");
        span?.SetTag("refs.matched", residuals.Count);
        return new DriftCheckOutcome.Inconclusive("too few visible landmarks", residuals.Count);
    }

    var maxResidual = residuals.Max(t => t.Dist);
    var threshold = DriftToleranceFactor * stored.ResidualPixels;
    span?.SetTag("map.area", sceneRef.MapAssetKey);
    span?.SetTag("refs.matched", residuals.Count);
    span?.SetTag("max_residual_px", maxResidual);
    span?.SetTag("threshold_px", threshold);

    if (maxResidual > threshold)
    {
        _logger?.LogWarning(
            "Drift check {MapAssetKey}: DRIFT detected ({Matched} refs matched, max residual {MaxResid:0.00}px exceeds threshold {Threshold:0.00}px). Hotkey armed for {Arm}s — re-press to recalibrate.",
            sceneRef.MapAssetKey, residuals.Count, maxResidual, threshold, ManualCalibrationCoordinator.ArmingSeconds);
        span?.SetTag("outcome", "Drift");
        return new DriftCheckOutcome.Drift(maxResidual, residuals.Count, threshold);
    }

    _logger?.LogInformation(
        "Drift check {MapAssetKey}: OK ({Matched} refs matched, max residual {MaxResid:0.00}px, threshold {Threshold:0.00}px). No recalibration needed.",
        sceneRef.MapAssetKey, residuals.Count, maxResidual, threshold);
    span?.SetTag("outcome", "Ok");
    return new DriftCheckOutcome.Ok(maxResidual, residuals.Count);
}
```

Note `ManualCalibrationCoordinator.ArmingSeconds` — this is declared in Task C2 as `public const int ArmingSeconds = 10;`. To avoid a forward reference: declare it on `AutoCalibrationEngine` instead as `internal const int DriftArmingSeconds = 10` and have `ManualCalibrationCoordinator` import it. Either direction is fine; pick whichever the engineer prefers, as long as both files agree.

- [ ] **Step 3.5: Add `DetectOnly` to `IMapCalibrationSolver` (if absent)**

If `IMapCalibrationSolver` doesn't already expose a detect-only entry point, add one. The interface adds:

```csharp
IReadOnlyList<TypedDetection> DetectOnly(DetectionRequest request);
```

The concrete `MapCalibrationSolveEngine` factors its existing detection invocation into `DetectOnly` and has `Solve` call it then run the geometric step. Tests that already wire fakes for `IMapCalibrationSolver` get a no-op `DetectOnly` implementation that returns the test's pre-seeded detection list.

- [ ] **Step 4: Replace the `NotImplementedException` helper stubs with concrete bodies**

```csharp
internal static class TestReferences
{
    // Six landmarks placed at known world coords on a 200x200 metre area.
    // The stored AreaCalibration in Stored() projects world → texture at
    // 1.0 px/m with origin (100,100), so these world coords map to texture
    // pixels (100+x, 100-z) per the engine's projection formula.
    public static readonly IReadOnlyList<CalibrationReference> Six = new[]
    {
        new CalibrationReference("Landmark1", "Landmark", new WorldCoord( 10, 0,   5)),
        new CalibrationReference("Landmark2", "Landmark", new WorldCoord(-20, 0,  15)),
        new CalibrationReference("Landmark3", "Landmark", new WorldCoord( 30, 0, -25)),
        new CalibrationReference("Landmark4", "Landmark", new WorldCoord(-40, 0, -10)),
        new CalibrationReference("Landmark5", "Landmark", new WorldCoord(  5, 0,  35)),
        new CalibrationReference("Landmark6", "Landmark", new WorldCoord(-15, 0,  20)),
    };
}

internal sealed class FakeDetector
{
    public IReadOnlyList<TypedDetection> Detections { get; }
    public FakeDetector(IReadOnlyList<TypedDetection> detections) { Detections = detections; }

    /// <summary>
    /// Build a detection set co-located with TestReferences.Six's predicted
    /// screen positions, with each detection offset by <paramref name="offsetPx"/>
    /// pixels. Uses the same compose formula CheckDriftAsync evaluates:
    /// predictedScreen = stored.WorldToWindow(world,1.0) * loc.Scale + (loc.Tx, loc.Ty).
    /// Stored() has Scale=1, Origin=(100,100), MirrorNorth=false, so the
    /// texture-space prediction collapses to (100 + worldX, 100 - worldZ).
    /// LocateMetrics scale=1, Tx=Ty=0 in this fake → screen == texture.
    /// </summary>
    public static FakeDetector AtPredictedPositions(double offsetPx)
    {
        var detections = new List<TypedDetection>();
        foreach (var r in TestReferences.Six)
        {
            var x = 100 + r.World.X + offsetPx;
            var y = 100 - r.World.Z + offsetPx;
            detections.Add(new TypedDetection(X: x, Y: y, TypeId: 0, Score: 1.0));
        }
        return new FakeDetector(detections);
    }

    /// <summary>
    /// Detections covering only the first <paramref name="n"/> references in
    /// TestReferences.Six. Used to exercise the "fewer than 3 matched"
    /// Inconclusive path.
    /// </summary>
    public static FakeDetector WithDetectionsAtFirstNPredictions(int n, double offsetPx)
    {
        var detections = new List<TypedDetection>();
        foreach (var r in TestReferences.Six.Take(n))
        {
            var x = 100 + r.World.X + offsetPx;
            var y = 100 - r.World.Z + offsetPx;
            detections.Add(new TypedDetection(X: x, Y: y, TypeId: 0, Score: 1.0));
        }
        return new FakeDetector(detections);
    }
}

internal sealed class FakeMapRegionRefiner : IMapRegionRefiner
{
    private readonly MapRegionRefineResult _result;
    public FakeMapRegionRefiner(MapRegionRefineResult result) { _result = result; }
    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture) => _result;
}

internal static class FakeRefiner
{
    public static IMapRegionRefiner RejectMap(string reason) =>
        new FakeMapRegionRefiner(new MapRegionRefineResult(
            AcceptedRect: null,
            RawFitRect: null,
            Metrics: new LocateMetrics(InlierCount: 2, CandidateCount: 30, InlierRatio: 0.067,
                Scale: 1.0, RotationDegrees: 0, Mirror: false, Tx: 0, Ty: 0, ResidualPixels: 4.0)));

    public static IMapRegionRefiner AcceptWithMetrics(LocateMetrics m) =>
        new FakeMapRegionRefiner(new MapRegionRefineResult(
            AcceptedRect: new MapRect(0, 0, 400, 400, 400, 400),
            RawFitRect: new MapRect(0, 0, 400, 400, 400, 400),
            Metrics: m));
}

internal static class FakeCapture
{
    public static ICaptureService ReturnNullGray() => new NullCapture();
    public static ICaptureService AcceptingCapture() => new AcceptingCaptureService();

    private sealed class NullCapture : ICaptureService
    {
        public Task<CaptureMapResult> CaptureMapAsync(CaptureRect rect, CancellationToken ct)
            => Task.FromResult(new CaptureMapResult(Gray: null, Color: null));
    }

    private sealed class AcceptingCaptureService : ICaptureService
    {
        public Task<CaptureMapResult> CaptureMapAsync(CaptureRect rect, CancellationToken ct)
            => Task.FromResult(new CaptureMapResult(
                Gray: new GrayImage(new byte[400 * 400], 400, 400),
                Color: null));
    }
}

internal sealed class FakeCalibrationSolver : IMapCalibrationSolver
{
    public IReadOnlyList<TypedDetection> SeededDetections { get; init; } = Array.Empty<TypedDetection>();
    public IReadOnlyList<TypedDetection> DetectOnly(DetectionRequest request) => SeededDetections;
    public CalibrationSolveResult Solve(DetectionRequest request, IReadOnlyList<CalibrationReference> refs)
        => throw new InvalidOperationException("Drift check path must not invoke Solve.");
}
```

Tests inject `FakeCalibrationSolver` (with `SeededDetections = fakeDetector.Detections`) as the `IMapCalibrationSolver` in `NewEngine`. `NewEngine` constructs the engine by passing `FakeCapture.AcceptingCapture()` as `ICaptureService`, the `FakeRefiner.AcceptWithMetrics(default-metrics-with-scale-1-Tx-0-Ty-0)` as `IMapRegionRefiner`, and the rest of the existing `EngineFakes.cs` stubs for `IMapState`, `ISceneAssetCache`, `IGameWindowLocator`, etc. The `cal` parameter is fed via a stub `IMapCalibrationService.GetCalibration` returning it.

- [ ] **Step 5: Run drift-check tests; expect PASS**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter FullyQualifiedName~AutoCalibrationEngineDriftCheckTests`

Expected: 8 PASS.

- [ ] **Step 6: Remove the `throw new NotImplementedException` stub from Task B2**

Delete the stub `CheckDriftAsync` body (the real implementation from Step 3 replaces it).

- [ ] **Step 7: Run the full engine test suite**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests`

Expected: existing tests still pass; drift-check tests pass; no new failures.

- [ ] **Step 8: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs src/Mithril.MapCalibration.Capture/IMapCalibrationSolver.cs src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineDriftCheckTests.cs
git commit -m "feat(map-calibration): CheckDriftAsync implementation + IMapCalibrationSolver.DetectOnly seam (mithril#1046)"
```

### Task B5: Update `docs/perf-trace-schema.md`

**Files:**
- Modify: `docs/perf-trace-schema.md`

- [ ] **Step 1: Read the existing `calibration.attempt` shape**

Read `docs/perf-trace-schema.md` and find the `calibration.attempt` span entry.

- [ ] **Step 2: Add `calibration.drift_check` alongside it**

```markdown
### `calibration.drift_check`

Wraps one `AutoCalibrationEngine.CheckDriftAsync` invocation (mithril#1046 §9.5).
Emitted on every manual hotkey press over a scene with a stored calibration.

| Tag | Type | Notes |
|---|---|---|
| `map.area` | string | `MapSceneRef.MapAssetKey` for the resolved scene (e.g. `"Map_AreaSerbule"`). |
| `refs.matched` | int | Count of reference landmarks that paired with a typed detection within the 20 px match gate. |
| `max_residual_px` | double | Worst predicted-vs-detected residual across the matched references. Compared against the threshold. |
| `threshold_px` | double | `DriftToleranceFactor × stored.ResidualPixels` (currently 3.0×). |
| `outcome` | string | One of `"NoStoredCalibration"`, `"CaptureFailed"`, `"MapNotLocated"`, `"NoIconDetections"`, `"Inconclusive"`, `"Ok"`, `"Drift"`. |
```

- [ ] **Step 3: Commit**

```bash
git add docs/perf-trace-schema.md
git commit -m "docs(perf-trace-schema): document calibration.drift_check span (mithril#1046)"
```

---

## Group C — Manual hotkey coordinator

### Task C1: New chip messages in `CalibrationStatusFormatter`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs`

- [ ] **Step 1: Read the existing formatter to understand its output API**

Read `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs`. Note whether it has a single `ForOutcome(AutoCalibrationOutcome)` method or per-category entry points. The new messages live as new methods or new branches in the existing one.

- [ ] **Step 2: Add the drift + recalibrate messages**

Add per spec §6.5:

```csharp
public static string DriftCheckOk() =>
    "Calibration check OK — no drift detected.";

public static string DriftCheckInconclusive(string reason) =>
    $"Drift check inconclusive — {reason}.";

public static string DriftDetected(double maxResidualPx, int armingSeconds) =>
    $"Drift detected (~{maxResidualPx:0.0}px). Press calibrate hotkey again within {armingSeconds}s to recalibrate.";

public static string DriftCheckCaptureFailed(string reason) =>
    $"Drift check: {reason}.";

public static string RecalibratedSuccessfully() =>
    "Recalibrated successfully.";
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Mithril.MapCalibration.Capture`

Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs
git commit -m "feat(map-calibration): chip messages for drift-check + recalibrate (mithril#1046)"
```

### Task C2: Failing coordinator tests

**Files:**
- Create: `tests/Mithril.MapCalibration.Capture.Tests/ManualCalibrationCoordinatorTests.cs`

- [ ] **Step 1: Write the test class with all 7 cases from spec §10.3**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class ManualCalibrationCoordinatorTests
{
    private const string Asset = "Map_AreaTest";
    private static readonly MapSceneRef Scene = new("AreaTest", null, Asset);

    private sealed class FakeRunner : IAutoCalibrationRunner
    {
        public int SolveCalls;
        public int DriftCalls;
        public DriftCheckOutcome DriftReturn = new DriftCheckOutcome.Ok(0.5, 6);
        public AutoCalibrationOutcome SolveReturn = new(Persisted: true, AreaKey: Asset, RejectReason: null, OutcomeCategory: "Accepted");
        public Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct) { SolveCalls++; return Task.FromResult(SolveReturn); }
        public Task<DriftCheckOutcome> CheckDriftAsync(CancellationToken ct) { DriftCalls++; return Task.FromResult(DriftReturn); }
    }

    private sealed class FakeClock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch; public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class FakeMapCal : IMapCalibrationService
    {
        private readonly AreaCalibration? _cal;
        public FakeMapCal(AreaCalibration? cal) { _cal = cal; }
        public static FakeMapCal With(AreaCalibration cal) => new(cal);
        public static FakeMapCal WithNoCalibration() => new(null);

        public bool IsCalibrated(MapSceneRef scene) => _cal is not null;
        public AreaCalibration? GetCalibration(MapSceneRef scene) => _cal;
        public PixelPoint? WorldToWindow(MapSceneRef scene, WorldCoord world, double currentZoom) =>
            _cal?.WorldToWindow(world, currentZoom);
        public WorldCoord? WindowToWorld(MapSceneRef scene, PixelPoint pixel, double currentZoom) =>
            _cal?.WindowToWorld(pixel, currentZoom);
        public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations =>
            _cal is null
                ? new Dictionary<string, AreaCalibration>()
                : new Dictionary<string, AreaCalibration> { [Asset] = _cal };
        public IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene) =>
            _cal is null ? Array.Empty<AreaCalibration>() : new[] { _cal };
        public void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration)
            => throw new InvalidOperationException("Coordinator must not persist directly.");
        public void ClearUserRefinement(MapSceneRef scene)
            => throw new InvalidOperationException("Coordinator must not clear directly.");
        public event EventHandler<MapSceneRef>? Changed;
    }

    // FakeMapState and FakeSceneAssetCache come from the existing
    // tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs
    // (internal sealed in the same test project). NewCoordinator below
    // instantiates them directly. Set FakeMapState.CurrentMapScene = Scene
    // for tests that need a known scene; otherwise leave default null.

    [Fact]
    public async Task Hotkey_NoStoredCalibration_RunsFullSolve()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.NoStoredCalibration() };
        var coordinator = NewCoordinator(runner, mapCal: FakeMapCal.WithNoCalibration());
        await coordinator.HandleHotkeyAsync(CancellationToken.None);
        runner.SolveCalls.Should().Be(1);
        runner.DriftCalls.Should().Be(0);
    }

    [Fact]
    public async Task Hotkey_DriftOk_DoesNotArmDoesNotSolve()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.Ok(0.5, 6) };
        var overlay = new FakeOverlayWindow();
        var coordinator = NewCoordinator(runner, mapCal: FakeMapCal.With(Stored()), overlay: overlay);
        await coordinator.HandleHotkeyAsync(CancellationToken.None);
        runner.SolveCalls.Should().Be(0);
        runner.DriftCalls.Should().Be(1);
        overlay.StatusMessage.Should().Contain("OK");
    }

    [Fact]
    public async Task Hotkey_Drift_ArmsAndSetsChip()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.Drift(5.0, 6, 2.1) };
        var overlay = new FakeOverlayWindow();
        var coordinator = NewCoordinator(runner, mapCal: FakeMapCal.With(Stored()), overlay: overlay);
        await coordinator.HandleHotkeyAsync(CancellationToken.None);
        coordinator.IsArmed.Should().BeTrue();
        overlay.StatusMessage.Should().Contain("Drift detected").And.Contain("Press calibrate hotkey again");
    }

    [Fact]
    public async Task Hotkey_ArmedRePressWithinWindow_RunsFullSolve()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.Drift(5.0, 6, 2.1) };
        var clock = new FakeClock { Now = DateTimeOffset.UnixEpoch };
        var coordinator = NewCoordinator(runner, mapCal: FakeMapCal.With(Stored()), clock: clock);
        await coordinator.HandleHotkeyAsync(CancellationToken.None); // arm
        coordinator.IsArmed.Should().BeTrue();
        clock.Now = clock.Now.AddSeconds(5);
        await coordinator.HandleHotkeyAsync(CancellationToken.None); // re-press
        runner.SolveCalls.Should().Be(1);
        coordinator.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task Hotkey_ArmedRePressAfterWindow_RunsDriftCheckAgain()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.Drift(5.0, 6, 2.1) };
        var clock = new FakeClock { Now = DateTimeOffset.UnixEpoch };
        var coordinator = NewCoordinator(runner, mapCal: FakeMapCal.With(Stored()), clock: clock);
        await coordinator.HandleHotkeyAsync(CancellationToken.None); // arm
        clock.Now = clock.Now.AddSeconds(11); // past 10s window
        runner.DriftCalls = 0; // reset counter for clarity
        await coordinator.HandleHotkeyAsync(CancellationToken.None);
        runner.DriftCalls.Should().Be(1);
        runner.SolveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Hotkey_LogsArmedAndExpired()
    {
        var logger = new CapturingLogger();
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.Drift(5.0, 6, 2.1) };
        var clock = new FakeClock { Now = DateTimeOffset.UnixEpoch };
        var coordinator = NewCoordinator(runner, mapCal: FakeMapCal.With(Stored()), clock: clock, logger: logger);
        await coordinator.HandleHotkeyAsync(CancellationToken.None); // arm
        clock.Now = clock.Now.AddSeconds(11); // expire
        await coordinator.HandleHotkeyAsync(CancellationToken.None); // fresh check
        logger.Entries.Should().Contain(e => e.Message.Contains("arming window expired"));
    }

    [Fact]
    public async Task Hotkey_NoBboxOrPgNotForeground_SurfacesActionableChip()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.CaptureFailed("no map bbox set") };
        var overlay = new FakeOverlayWindow();
        var coordinator = NewCoordinator(runner, mapCal: FakeMapCal.With(Stored()), overlay: overlay);
        await coordinator.HandleHotkeyAsync(CancellationToken.None);
        coordinator.IsArmed.Should().BeFalse();
        overlay.StatusMessage.Should().Contain("no map bbox set");
    }

    // ---- helper ----
    private static AreaCalibration Stored() =>
        new(Scale: 1.0, RotationRadians: 0, OriginX: 100, OriginY: 100,
            ReferenceCount: 6, ResidualPixels: 0.7)
        { Source = CalibrationSource.AutoCapture };

    private static ManualCalibrationCoordinator NewCoordinator(
        IAutoCalibrationRunner runner,
        IMapCalibrationService mapCal,
        TimeProvider? clock = null,
        IOverlayWindow? overlay = null,
        CapturingLogger? logger = null) =>
        new(
            runner: runner,
            calibrationService: mapCal,
            mapState: new FakeMapState { CurrentMapScene = Scene },
            sceneCache: new FakeSceneAssetCache(),
            overlay: overlay ?? new FakeOverlayWindow(),
            timeProvider: clock ?? new FakeClock(),
            logger: (ILogger?)logger ?? NullLogger.Instance);
}
```

`FakeMapState`, `FakeSceneAssetCache`, and `FakeOverlayWindow` are the internal types already defined in `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs` — no duplicate declarations needed in the coordinator test class.

- [ ] **Step 2: Build — expect compile failure (no `ManualCalibrationCoordinator` yet)**

Run: `dotnet build tests/Mithril.MapCalibration.Capture.Tests`

Expected: CS0246 "type or namespace name 'ManualCalibrationCoordinator' could not be found".

- [ ] **Step 3: Commit failing tests** (skip — won't build; combine with C3 commit instead)

### Task C3: Implement `ManualCalibrationCoordinator`

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/ManualCalibrationCoordinator.cs`

- [ ] **Step 1: Write the coordinator**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.Overlay;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Owns the manual calibrate hotkey's state machine (mithril#1046 §6.4). On
/// each press: either re-press-armed → run the full solve, or run a drift
/// check; route the outcome through <see cref="CalibrationStatusFormatter"/>
/// to the overlay status chip. Arming is in-process only — a restart disarms.
/// </summary>
public sealed class ManualCalibrationCoordinator
{
    public const int ArmingSeconds = 10;
    private static readonly TimeSpan ArmingWindow = TimeSpan.FromSeconds(ArmingSeconds);

    private readonly IAutoCalibrationRunner _runner;
    private readonly IMapCalibrationService _calibrationService;
    private readonly IMapState _mapState;
    private readonly ISceneAssetCache _sceneCache;
    private readonly IOverlayWindow _overlay;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;

    private DateTimeOffset? _armedUntil;
    private readonly object _gate = new();

    public ManualCalibrationCoordinator(
        IAutoCalibrationRunner runner,
        IMapCalibrationService calibrationService,
        IMapState mapState,
        ISceneAssetCache sceneCache,
        IOverlayWindow overlay,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        _runner = runner;
        _calibrationService = calibrationService;
        _mapState = mapState;
        _sceneCache = sceneCache;
        _overlay = overlay;
        _time = timeProvider;
        _logger = logger;
    }

    public bool IsArmed
    {
        get
        {
            lock (_gate) return _armedUntil is { } until && _time.GetUtcNow() < until;
        }
    }

    public async Task HandleHotkeyAsync(CancellationToken ct)
    {
        var scene = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache);
        var armed = ConsumeIfArmed();

        var storedSource = scene is { } s ? _calibrationService.GetCalibration(s)?.Source : null;
        _logger?.LogInformation(
            "Manual calibrate hotkey: scene={MapAssetKey}, armed={IsArmed}, storedSource={Source}.",
            scene?.MapAssetKey ?? "<none>", armed, storedSource?.ToString() ?? "<none>");

        if (armed)
        {
            _logger?.LogInformation("Manual calibrate hotkey: armed re-press confirmed; running full solve.");
            var outcome = await _runner.TryCalibrateCurrentAreaAsync(ct).ConfigureAwait(false);
            _overlay.SetStatusMessage(outcome.Persisted
                ? CalibrationStatusFormatter.RecalibratedSuccessfully()
                : CalibrationStatusFormatter.ForOutcome(outcome));
            return;
        }

        if (scene is null)
        {
            // No scene yet — engine will surface the actionable reject reason.
            var outcome = await _runner.TryCalibrateCurrentAreaAsync(ct).ConfigureAwait(false);
            _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(outcome));
            return;
        }

        var stored = _calibrationService.GetCalibration(scene.Value);
        if (stored is null)
        {
            // Cold path — no drift check, just solve.
            var outcome = await _runner.TryCalibrateCurrentAreaAsync(ct).ConfigureAwait(false);
            _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(outcome));
            return;
        }

        var drift = await _runner.CheckDriftAsync(ct).ConfigureAwait(false);
        switch (drift)
        {
            case DriftCheckOutcome.Ok:
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftCheckOk());
                break;
            case DriftCheckOutcome.Inconclusive inc:
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftCheckInconclusive(inc.Reason));
                break;
            case DriftCheckOutcome.Drift d:
                lock (_gate) _armedUntil = _time.GetUtcNow() + ArmingWindow;
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftDetected(d.MaxResidualPx, ArmingSeconds));
                break;
            case DriftCheckOutcome.CaptureFailed cf:
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftCheckCaptureFailed(cf.Reason));
                break;
            case DriftCheckOutcome.MapNotLocated mnl:
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftCheckCaptureFailed(mnl.Reason));
                break;
            case DriftCheckOutcome.NoIconDetections:
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftCheckInconclusive("no icons detected in captured frame"));
                break;
            case DriftCheckOutcome.NoStoredCalibration:
                // Race: stored existed at our pre-check but is gone now. Fall through to solve.
                var fallbackOutcome = await _runner.TryCalibrateCurrentAreaAsync(ct).ConfigureAwait(false);
                _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(fallbackOutcome));
                break;
        }
    }

    private bool ConsumeIfArmed()
    {
        lock (_gate)
        {
            if (_armedUntil is { } until)
            {
                if (_time.GetUtcNow() < until)
                {
                    _armedUntil = null;
                    return true;
                }
                _logger?.LogInformation(
                    "Manual calibrate hotkey: drift arming window expired ({Arm}s).", ArmingSeconds);
                _armedUntil = null;
            }
            return false;
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`

Expected: success.

- [ ] **Step 3: Run coordinator tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter FullyQualifiedName~ManualCalibrationCoordinatorTests`

Expected: 7 PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/ManualCalibrationCoordinator.cs tests/Mithril.MapCalibration.Capture.Tests/ManualCalibrationCoordinatorTests.cs
git commit -m "feat(map-calibration): ManualCalibrationCoordinator for verify-and-warn hotkey (mithril#1046)"
```

### Task C4: Rewire `CaptureCalibrateCommand` to delegate to the coordinator

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Hotkeys/CaptureCalibrateCommand.cs`
- Modify: `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`

- [ ] **Step 1: Read the current hotkey command**

Read `src/Mithril.MapCalibration.Capture/Hotkeys/CaptureCalibrateCommand.cs` to understand its current shape (`IHotkeyCommand` impl + a method that calls `_runner.TryCalibrateCurrentAreaAsync`).

- [ ] **Step 2: Replace the runner call with a coordinator call**

```csharp
public sealed class CaptureCalibrateCommand : IHotkeyCommand
{
    private readonly ManualCalibrationCoordinator _coordinator;
    private readonly ILogger? _logger;

    public CaptureCalibrateCommand(ManualCalibrationCoordinator coordinator, ILogger<CaptureCalibrateCommand>? logger = null)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    // Existing IHotkeyCommand surface: Id, DisplayName, DefaultShortcut, etc.
    // Body of the hotkey-fire path:
    public async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await _coordinator.HandleHotkeyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Manual calibrate hotkey threw; chip will not update.");
        }
    }
}
```

- [ ] **Step 3: Register the coordinator in DI**

In `CaptureServiceCollectionExtensions.cs`, register `ManualCalibrationCoordinator` as singleton. Ensure `TimeProvider.System` is available (Mithril's host registers it; if not, add `services.TryAddSingleton<TimeProvider>(TimeProvider.System);`).

```csharp
services.AddSingleton<ManualCalibrationCoordinator>();
services.TryAddSingleton<TimeProvider>(TimeProvider.System); // if not already registered upstream
```

- [ ] **Step 4: Build + run full test suite**

Run: `dotnet build && dotnet test Mithril.slnx`

Expected: all pass.

- [ ] **Step 5: Smoke-run the shell manually**

Run: `dotnet run --project src/Mithril.Shell`

In-game: zone into Serbule, ensure the picker still works (chips/overlay render against the stored calibration); press the manual calibrate hotkey on a calibrated scene; observe the chip says "Calibration check OK — no drift detected." Repeat on a known-bad capture (deliberately uncalibrated) → "no map bbox set" or similar actionable reject.

Per spec §12, the chip render path is under independent suspicion — even if the chip doesn't render visibly, check `boot.log` for the matching `Information`-level lines. Both observability surfaces (chip + log) should agree.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Hotkeys/CaptureCalibrateCommand.cs src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs
git commit -m "feat(map-calibration): wire manual calibrate hotkey to ManualCalibrationCoordinator (mithril#1046)"
```

---

### 🟢 Code review gate B — verify-and-warn flow

**Stop. Request review before proceeding to Group D.**

Diff scope: new `DriftCheckOutcome.cs`, `ManualCalibrationCoordinator.cs`; modifications to `AutoCalibrationEngine.cs`, `IAutoCalibrationRunner.cs`, `IMapCalibrationSolver.cs`, `CalibrationStatusFormatter.cs`, `CaptureCalibrateCommand.cs`, `CaptureServiceCollectionExtensions.cs`; new tests; doc update.

What the reviewer should check:
- DriftCheck composition math matches §6.2: `predictedTexture = stored.WorldToWindow(refWorld, 1.0); predictedScreen = predictedTexture * LocateMetrics.Scale + (Tx, Ty)`.
- Arming-window state-machine corner cases (consume-on-press, expire-on-window-end, fresh-check after expiration).
- Chip messages match §6.5 exactly.
- Logging contract (§9.2, §9.3) is exhaustive — every decision point has a log line at the spec'd level.
- No silent failures in the coordinator (every code path either solves, drift-checks, or sets a chip message).
- The smoke-run note in Task C4 Step 5 is acknowledged: chip suspected unreliable, log is the source of truth.

Do not start Group D until the reviewer signs off.

---

## Group D — Trigger pre-flight + dead-code removal

### Task D1: Failing trigger tests for the new pre-flight

**Files:**
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationTriggerTests.cs`

- [ ] **Step 1: Read the existing trigger tests to understand setup conventions**

Read `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationTriggerTests.cs`. Identify the existing `FakeMapCal` (or whatever stub is used).

- [ ] **Step 2: Add the 5 cases from spec §10.4**

```csharp
[Fact]
public async Task Trigger_StoreHasUserRefinement_Skips()
{
    var runner = new SpyRunner();
    var mapCal = new FakeMapCal(sources: new[] { Cal(0.8, 6, CalibrationSource.UserRefinement) });
    var logger = new CapturingLogger();
    var trigger = NewTrigger(runner, mapCal, logger);
    await trigger.OnSceneChangedAsync(Scene);
    runner.Calls.Should().Be(0);
    logger.Entries.Should().Contain(e => e.Message.Contains("store has UserRefinement record"));
}

[Fact]
public async Task Trigger_StoreHasAutoCapture_Skips()
{
    var runner = new SpyRunner();
    var mapCal = new FakeMapCal(sources: new[] { Cal(0.6, 5, CalibrationSource.AutoCapture) });
    var logger = new CapturingLogger();
    var trigger = NewTrigger(runner, mapCal, logger);
    await trigger.OnSceneChangedAsync(Scene);
    runner.Calls.Should().Be(0);
    logger.Entries.Should().Contain(e => e.Message.Contains("store has AutoCapture record"));
}

[Fact]
public async Task Trigger_StoreOnlyHasBundledBaseline_Fires()
{
    var runner = new SpyRunner();
    var mapCal = new FakeMapCal(sources: new[] { Cal(2.1, 6, CalibrationSource.BundledBaseline) });
    var trigger = NewTrigger(runner, mapCal);
    await trigger.OnSceneChangedAsync(Scene);
    runner.Calls.Should().Be(1);
}

[Fact]
public async Task Trigger_StoreEmpty_Fires()
{
    var runner = new SpyRunner();
    var mapCal = new FakeMapCal(sources: Array.Empty<AreaCalibration>());
    var trigger = NewTrigger(runner, mapCal);
    await trigger.OnSceneChangedAsync(Scene);
    runner.Calls.Should().Be(1);
}

[Fact]
public async Task Trigger_PickerReturnsBaselineButStoreHasAuto_Skips()
{
    // Baseline is better-quality so the picker prefers it, but the store
    // ALSO has an AutoCapture record — the trigger respects the store, not
    // the picker.
    var runner = new SpyRunner();
    var mapCal = new FakeMapCal(
        sources: new[]
        {
            Cal(0.5, 8, CalibrationSource.BundledBaseline), // picker would pick this
            Cal(1.2, 5, CalibrationSource.AutoCapture),     // but store-backed skip kicks in
        },
        picked: Cal(0.5, 8, CalibrationSource.BundledBaseline));
    var logger = new CapturingLogger();
    var trigger = NewTrigger(runner, mapCal, logger);
    await trigger.OnSceneChangedAsync(Scene);
    runner.Calls.Should().Be(0);
    logger.Entries.Should().Contain(e => e.Message.Contains("picker returned BundledBaseline"));
}
```

Augment `FakeMapCal` with a `GetAllSources` returning the configured list and `GetCalibration` returning the configured `picked` (or null).

- [ ] **Step 3: Run; expect 5 to FAIL (trigger still uses GetCalibration today)**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter FullyQualifiedName~AutoCalibrationTriggerTests`

Expected: new 5 cases fail; existing pass.

- [ ] **Step 4: Commit failing tests**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationTriggerTests.cs
git commit -m "test(map-calibration): failing trigger pre-flight tests using GetAllSources (mithril#1046)"
```

### Task D2: Update `AutoCalibrationTrigger.OnSceneChangedAsync` pre-flight

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs`

- [ ] **Step 1: Replace the pre-flight check at lines ~157–161**

```csharp
// Skip if the store has any UserRefinement or AutoCapture record for this
// scene. Decoupled from GetCalibration's picker: the picker may return a
// BundledBaseline when its residual+ref-count beats a stored AutoCapture,
// but the trigger's promise is "one cold solve per scene per install"
// (mithril#1046 §7).
var sources = _calibrationService.GetAllSources(scene);
var converged = sources.FirstOrDefault(s => s.Source is CalibrationSource.UserRefinement or CalibrationSource.AutoCapture);
if (converged is not null)
{
    _logger.LogInformation(
        "Auto-trigger skipped for {MapAssetKey}: store has {Source} record (residual {Residual:0.00}px, refs {Refs}). One-shot-per-install respected.",
        key, converged.Source, converged.ResidualPixels, converged.ReferenceCount);

    // Picker/store-disagreement telemetry — informational, surfaces how
    // often the picker prefers a baseline over a stored auto.
    var picked = _calibrationService.GetCalibration(scene);
    if (picked is not null && picked.Source != converged.Source)
    {
        _logger.LogInformation(
            "Auto-trigger skipped for {MapAssetKey}: store has converged solve (source={StoredSource}) but picker returned {PickedSource}. Picker chose better-quality record; trigger respects store.",
            key, converged.Source, picked.Source);
    }
    return;
}

_logger.LogInformation(
    "Auto-trigger firing for {MapAssetKey}: no converged solve in store; attempting cold solve (existing source: {Source}).",
    key, sources.FirstOrDefault()?.Source.ToString() ?? "<none>");
```

- [ ] **Step 2: Run trigger tests; expect PASS**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter FullyQualifiedName~AutoCalibrationTriggerTests`

Expected: 5 new + existing all PASS.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs
git commit -m "refactor(map-calibration): trigger pre-flight uses GetAllSources, decoupled from picker (mithril#1046)"
```

### Task D3: Delete the #988 monotonicity gate

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`

- [ ] **Step 1: Delete `CheckMonotonicAccept` method (lines ~676–688)**

Delete the static method body in full.

- [ ] **Step 2: Delete the `MonotonicResidualRatio` and `MonotonicInlierDelta` consts (lines ~69–70)**

- [ ] **Step 3: Delete the call site in `RunAttemptCoreAsync` (lines ~465–482)**

Remove the existing-fit lookup, the regime-guard `if` block, and the gate-body block. The accept path falls straight through to `SaveUserRefinement` at line ~484+.

- [ ] **Step 4: Build**

Run: `dotnet build`

Expected: success. Any test references to `CheckMonotonicAccept` should be deleted in Step 5.

- [ ] **Step 5: Delete monotonicity test cases from `AutoCalibrationEngineTests`**

Remove all test methods named `*Monotonic*` from `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs`.

- [ ] **Step 6: Run full test suite**

Run: `dotnet test Mithril.slnx`

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs
git commit -m "refactor(map-calibration): delete #988 monotonicity gate (mithril#1046)"
```

### Task D4: Delete the #1005 regime guard + `LocatorScale` field

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`
- Modify: `src/Mithril.MapCalibration/AreaCalibration.cs`
- Delete: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineZoomChangeRegressionTests.cs`

- [ ] **Step 1: Delete `IsSameScaleRegime` (lines ~656–661) and `ScaleRegimeRelTolerance` const (line ~79)**

- [ ] **Step 2: Delete the `LocatorScale = refineResult.Metrics?.Scale` stamp at lines ~450–454**

The `stamped` record construction shrinks to:

```csharp
var stamped = result.Calibration with
{
    Source = CalibrationSource.AutoCapture,
};
```

- [ ] **Step 3: Delete `AreaCalibration.LocatorScale` property (lines ~63–80)**

Read `src/Mithril.MapCalibration/AreaCalibration.cs:63-80` first to confirm exact line range. Remove the property and its xmldoc.

- [ ] **Step 4: Delete `AutoCalibrationEngineZoomChangeRegressionTests.cs` entirely**

```bash
git rm tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineZoomChangeRegressionTests.cs
```

- [ ] **Step 5: Delete any `*RegimeGuard*` test methods from `AutoCalibrationEngineTests.cs`**

Search the file; remove methods.

- [ ] **Step 6: Build**

Run: `dotnet build`

Expected: success. If any JsonSerializerContext source-gen references `LocatorScale` (it shouldn't, since System.Text.Json's source generator only emits properties declared on the type), regenerate via clean build:

```powershell
Remove-Item -Recurse -Force src/Mithril.MapCalibration/obj
dotnet build src/Mithril.MapCalibration
```

- [ ] **Step 7: Add JSON round-trip test for legacy `LocatorScale` property**

Add to `tests/Mithril.MapCalibration.Tests/UserRefinementStoreTests.cs` (create if absent):

```csharp
[Fact]
public void Load_IgnoresUnknownLocatorScaleProperty_FromPre1046Records()
{
    var legacyJson = """
        {
          "Version": 1,
          "Refinements": {
            "Map_AreaTest": {
              "Scale": 1.0,
              "RotationRadians": 0,
              "OriginX": 100,
              "OriginY": 100,
              "ReferenceCount": 6,
              "ResidualPixels": 0.7,
              "MirrorNorth": false,
              "CalibrationZoom": 1.0,
              "Source": "AutoCapture",
              "SchemaVersion": 1,
              "LocatorScale": 0.762
            }
          }
        }
        """;
        var tempPath = Path.Combine(Path.GetTempPath(), $"refinement-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, legacyJson);
        try
        {
            var store = new UserRefinementStore(tempPath, /* logger */ NullLogger.Instance);
            store.TryGet("Map_AreaTest", out var cal).Should().BeTrue();
            cal.ResidualPixels.Should().Be(0.7);

            // Round-trip: save a new record; assert no LocatorScale property in JSON.
            store.Save("Map_AreaOther", cal);
            var roundTripped = File.ReadAllText(tempPath);
            roundTripped.Should().NotContain("LocatorScale");
        }
        finally { File.Delete(tempPath); }
}
```

Adjust the `UserRefinementStore` ctor signature to match the type's actual constructor.

- [ ] **Step 8: Run full test suite**

Run: `dotnet test Mithril.slnx`

Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add src/Mithril.MapCalibration src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs tests/Mithril.MapCalibration.Tests/UserRefinementStoreTests.cs
git rm tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineZoomChangeRegressionTests.cs
git commit -m "refactor(map-calibration): delete #1005 regime guard + LocatorScale field (mithril#1046)"
```

### Task D5: Flip the planning-index row to shipped + smoke-run

**Files:**
- Modify: `docs/planning/INDEX.md`

- [ ] **Step 1: Update the row from `active` to `active` (no change yet)**

Hold off on flipping to `shipped` until the PR merges. This task is a placeholder for the post-merge index update — done in the same merge PR or a follow-up doc PR per project convention (see #1040 / #1044 precedent).

If shipping with the PR: edit the `calibration-1046-compose-dont-resolve` row's status to `shipped` and append the PR number:

```markdown
| [calibration-1046-compose-dont-resolve](calibration-1046-compose-dont-resolve/) | shipped | [#1046](https://github.com/moumantai-gg/mithril/issues/1046) · [#NNNN](https://github.com/moumantai-gg/mithril/pull/NNNN) | Compose-don't-resolve … |
```

- [ ] **Step 2: Build the shell + smoke-test the integrated flow**

```powershell
dotnet build Mithril.slnx
```

Run the shell:

```powershell
dotnet run --project src/Mithril.Shell
```

In-game:
1. Zone into a known-calibrated area (e.g. Serbule). The map should render against the picker's chosen calibration (check `boot.log` for the picker's Trace line).
2. Press the manual calibrate hotkey. Expected log line: `Drift check starting for Map_AreaSerbule...` followed by `Drift check Map_AreaSerbule: OK ...`.
3. Press the hotkey again. Same drift-check, no arming.
4. (Optional, manual disturbance) Move PG slightly so the captured icons don't align with predictions, then press hotkey. Expected: `Drift check Map_AreaSerbule: DRIFT detected ...` and chip says so. Re-press within 10 s. Expected: full solve runs.
5. Zone into a fresh area (no stored cal). The auto-trigger should fire (cold path), log line: `Auto-trigger firing for Map_AreaX: no converged solve in store`.

If the chip doesn't render on screen, the log lines are authoritative per spec §12.

- [ ] **Step 3: Commit any documentation tweaks**

```bash
git add docs/planning/INDEX.md
git commit -m "docs(planning): flip calibration-1046-compose-dont-resolve to shipped"
```

(Skip this commit if holding the status flip for post-merge.)

---

### 🟢 Code review gate C — trigger + dead-code

**Stop. Request review before opening the PR.**

Diff scope: `AutoCalibrationTrigger.cs`, `AutoCalibrationEngine.cs` deletions, `AreaCalibration.cs`, test additions/deletions, planning-index row.

What the reviewer should check:
- Trigger pre-flight uses `GetAllSources`, not `GetCalibration`; both Information logs (skip + cold-fire + picker-disagreement) are wired.
- All three dead pieces gone: `CheckMonotonicAccept` + consts, `IsSameScaleRegime` + const, `AreaCalibration.LocatorScale` + the stamp site.
- JSON round-trip test demonstrates legacy `LocatorScale` records load cleanly and the property doesn't re-emit.
- `AutoCalibrationEngineZoomChangeRegressionTests` file is deleted, not commented out.
- Smoke-run notes in Task D5 Step 2 were executed and the log evidence is acknowledged.

After sign-off, open the PR per CLAUDE.md routing: branch from `claude/zen-lovelace-18e306`, `gh pr create` against `main`, link `#1046` in the body.

---

## Self-review checklist (done before saving this plan)

- **Spec coverage:** §1–§3 covered by header/architecture; §4 by the file map + group order; §5 picker by Group A; §6 verify-and-warn by Groups B+C; §7 trigger by Task D1–D2; §8 dead code by Tasks D3–D4; §9 logging by inline templates in every relevant task; §10 test plan by Tasks A1/B3/C2/D1; §11 out of scope by explicit notes inside tasks where confusion is likely; §12 verification owed by §10.5 (JSON test) + Task C4 Step 5 (chip suspect) + Task D5 Step 2 (log-as-authority); §13 adjacent issues are referenced in commit-message tags via `mithril#1046`.
- **Placeholder scan:** no TBD/TODO/FIXME in step bodies. Two intentional "if the existing thing doesn't exist" branches (Task A1 Step 1 for the test project, Task A1 Step 3 for the `UserRefinementStore.ForTests` factory) — both spelled out with the action to take, not deferred to "later."
- **Type consistency:** `DriftCheckOutcome.Ok`, `DriftCheckOutcome.Drift`, `DriftCheckOutcome.Inconclusive`, `DriftCheckOutcome.NoStoredCalibration`, `DriftCheckOutcome.CaptureFailed`, `DriftCheckOutcome.MapNotLocated`, `DriftCheckOutcome.NoIconDetections` are used identically in Tasks B1, B3, B4, C2, C3. `ManualCalibrationCoordinator.ArmingSeconds` appears in B4 (forward ref noted) and C3 (declared). `IMapCalibrationSolver.DetectOnly` is added in B4 Step 3.5 and reused via the test fakes in B3/C2.

---

## Execution

**Plan complete and saved to [docs/planning/calibration-1046-compose-dont-resolve/plan.md](docs/planning/calibration-1046-compose-dont-resolve/plan.md). Two execution options:**

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task using `superpowers:subagent-driven-development`. Two-stage review per task; code-review gates A/B/C are explicit checkpoints with the reviewer agent.

2. **Inline Execution** — I execute tasks in this session using `superpowers:executing-plans`. Batch execution with checkpoint pauses at each `🟢 Code review gate`.

**Which approach?**
