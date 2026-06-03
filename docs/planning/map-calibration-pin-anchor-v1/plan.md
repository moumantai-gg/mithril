# Plan — map pins as auto-calibration anchors v1

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `AutoCalibrationEngine` succeed in areas whose only world-anchor references are user-placed map pins. Adds 2 icon templates, 2 `CanonicalLandmarkTypes` constants, 1 reference provider, 1 composite provider, 1 cold-start outcome, and 1 shared `MapPinDescriptor` helper. Lands as a single squash-merged PR.

**Architecture:** A new `IAreaReferenceProviderSource` (`MapPinAreaReferenceProvider`) reads `IMapPinState.Pins` and emits `LandmarkReference`s typed by Shape (`MapPinCircle` / `MapPinSquare`). A new `CompositeAreaReferenceProvider` concatenates pin + reference-data sources behind the existing `IAreaReferenceProvider` seam consumed by `AutoCalibrationEngine`. The engine gains a cold-start hint (`RejectedNeedsMorePins`) when refs are pin-only and below 3. Two new icon-template entries (`MapPin_Circle`, `MapPin_Square`) extend the bundled asset blob; the canonical-asset-hash gate is updated. `Arda.Contracts.State.Player.MapPinDescriptor` is a pure shared decoder for Shape/Color/Name display, consumed by the pin provider here and by a sibling Palantir issue.

**Tech Stack:** .NET 10 (`net10.0-windows`), C# 13, MSBuild `Mithril.slnx`, xUnit + FluentAssertions, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection.

**Spec:** [`docs/planning/map-calibration-pin-anchor-v1/spec.md`](spec.md). Decisions D1–D9 are ratified there; this plan does not re-litigate them.

**Issue:** [mithril#1036](https://github.com/moumantai-gg/mithril/issues/1036). Lands as a single squash-merged PR against `main`.

**Upstream dependency:** [mithril#1021](https://github.com/moumantai-gg/mithril/issues/1021) (per-scene calibration keying) — the `IAreaReferenceProvider.ForArea(string)` → `ForArea(MapSceneRef)` rename. If #1021 has not merged at land time, the small seam rename + `MapSceneRef` shim ride along in this PR (see Phase 4 note). No merge race; rebase is mechanical.

**Sibling follow-ups** (filed as separate issues, not blockers):
- [mithril#1037](https://github.com/moumantai-gg/mithril/issues/1037) — Palantir `WorldStateView` decode via `MapPinDescriptor`.
- [mithril#1038](https://github.com/moumantai-gg/mithril/issues/1038) — Pin arg-A investigation (visibility flag in aggregator subzones).

---

## Build / test cheat sheet

```bash
# Build everything (warnings as errors enforced; CleanBinObj clears stale obj/ first)
dotnet build Mithril.slnx

# Run all tests
dotnet test Mithril.slnx

# Run one test project
dotnet test tests/Mithril.MapCalibration.Capture.Tests

# Run one test by FQN substring
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~MapPinAreaReferenceProviderTests"

# Re-extract bundled icon templates (after the IconTemplateExtractor allowlist widen)
dotnet run --project tools/Mithril.AssetExtractor -c Release -- \
  --install "C:\Program Files (x86)\Steam\steamapps\common\Project Gorgon" \
  --out "%LocalAppData%\Mithril\assets" --icons \
  --tpk "%LocalAppData%\Mithril\assets\classdata.tpk"
```

> **Important — close Mithril.exe before building.** The repo's `check-mithril-running.ps1` PreToolUse hook blocks `dotnet build/test` while the shell is running (stale-DLL file-lock protection; memory `mithril_build_file_lock_silent`). If a build mysteriously fails with `MSB3026` / `MSB3027`, close Mithril first.

---

## Implementation order

Tasks land in dependency order so the build stays green between commits. Phases:

1. **Foundation** (Tasks 1–2) — `MapPinDescriptor` helper + tests. Standalone, consumed later by the pin provider and the sibling Palantir issue.
2. **Vocabulary** (Tasks 3–4) — `CanonicalLandmarkTypes` additions + tests. Shape-locks the type strings used downstream.
3. **Asset extraction** (Tasks 5–7) — `IconTemplateExtractor` allowlist widen + sidecar re-run + bundled blob regeneration + `CanonicalAssetHashes` update. After Phase 3 the detector can produce typed pin detections; refs are not yet wired.
4. **Reference provider seam** (Tasks 8–11) — `IAreaReferenceProviderSource` marker, `MapPinAreaReferenceProvider`, `CompositeAreaReferenceProvider`, refit `ReferenceDataAreaReferenceProvider`, DI wireup. **🔍 Review checkpoint A** at end of phase.
5. **Engine cold-start hint** (Tasks 12–14) — `AutoCalibrationEngine` code path + `OutcomeVocabulary.RejectedNeedsMorePins` + `CalibrationStatusFormatter` mapping + engine-level unit tests.
6. **Integration + regression tests** (Tasks 15–17) — synthetic-frame end-to-end tests; landmark-only byte-parity regression; sidecar extraction test (gated).
7. **Manual verification + INDEX flip + PR** (Tasks 18–20) — live captures in `AreaEltibule` (no-op) + `AreaCave1` (the headline behaviour); INDEX.md status flip; PR. **🔍 Review checkpoint B** before PR.

The two `🔍 Review checkpoint`s are the only places the plan calls for `superpowers:requesting-code-review`. Self-checks (build green, tests green) happen continuously; an explicit review is heavyweight and should fire at shape-lock moments only.

---

## Phase 1 — Foundation (shared decoder)

### Task 1: Add `MapPinDescriptor` helper

**Files:**
- New: `src/Arda/Arda.Contracts/State/Player/MapPinDescriptor.cs`

- [ ] **Step 1: Create the file with the three static methods**

```csharp
namespace Arda.World.Player;

/// <summary>
/// Decodes the integer Shape + Color fields on <see cref="MapPinEntry"/> to
/// human-readable strings, per the canonical tables documented in
/// docs/player-pin-service.md lines 52-53.
///
/// <para>Single source of truth: <see cref="MapPinAreaReferenceProvider"/>
/// (auto-calibration) and Palantir's WorldStateView (display, sibling issue)
/// both consume this helper rather than re-implementing the lookups inline.</para>
/// </summary>
public static class MapPinDescriptor
{
    public static string ShapeName(int shape) => shape switch
    {
        0 => "Dot",
        1 => "Square",
        _ => "Unknown",
    };

    public static string ColorName(int color) => color switch
    {
        0 => "White", 1 => "Red", 2 => "Orange", 3 => "Yellow", 4 => "Green",
        5 => "Cyan", 6 => "Blue", 7 => "Purple", 8 => "Pink", 9 => "Black",
        _ => "Unknown",
    };

    public static string Describe(MapPinEntry entry)
    {
        var prefix = $"{ColorName(entry.Color)} {ShapeName(entry.Shape)}";
        return string.IsNullOrWhiteSpace(entry.Label)
            ? prefix
            : $"{prefix} \"{entry.Label}\"";
    }
}
```

- [ ] **Step 2: Verify the build is green**

```bash
dotnet build src/Arda/Arda.Contracts
```

### Task 2: Unit tests for `MapPinDescriptor`

**Files:**
- New: `tests/Arda.Contracts.Tests/MapPinDescriptorTests.cs` (or the analogous existing project — verify the test-project layout for `Arda.Contracts` before naming).

- [ ] **Step 1: Cover the documented tables, the Unknown fallback, and label-empty / non-empty paths**

```csharp
public class MapPinDescriptorTests
{
    [Theory]
    [InlineData(0, "Dot")]
    [InlineData(1, "Square")]
    [InlineData(2, "Unknown")]
    [InlineData(-1, "Unknown")]
    [InlineData(int.MaxValue, "Unknown")]
    public void ShapeName_decodes_table(int shape, string expected)
        => MapPinDescriptor.ShapeName(shape).Should().Be(expected);

    [Theory]
    [InlineData(0, "White")]
    [InlineData(1, "Red")]
    [InlineData(2, "Orange")]
    [InlineData(3, "Yellow")]
    [InlineData(4, "Green")]
    [InlineData(5, "Cyan")]
    [InlineData(6, "Blue")]
    [InlineData(7, "Purple")]
    [InlineData(8, "Pink")]
    [InlineData(9, "Black")]
    [InlineData(10, "Unknown")]
    [InlineData(-1, "Unknown")]
    public void ColorName_decodes_table(int color, string expected)
        => MapPinDescriptor.ColorName(color).Should().Be(expected);

    [Fact]
    public void Describe_without_label_formats_color_then_shape()
    {
        var entry = new MapPinEntry(0, 0, "", Shape: 1, Color: 1);
        MapPinDescriptor.Describe(entry).Should().Be("Red Square");
    }

    [Fact]
    public void Describe_with_label_appends_quoted_label()
    {
        var entry = new MapPinEntry(0, 0, "South", Shape: 1, Color: 1);
        MapPinDescriptor.Describe(entry).Should().Be("Red Square \"South\"");
    }

    [Fact]
    public void Describe_with_unknown_shape_or_color_uses_fallback_strings()
    {
        var entry = new MapPinEntry(0, 0, "", Shape: 99, Color: 99);
        MapPinDescriptor.Describe(entry).Should().Be("Unknown Unknown");
    }
}
```

- [ ] **Step 2: `dotnet test tests/Arda.Contracts.Tests`** (or the resolved project name) **passes**.

---

## Phase 2 — Vocabulary

### Task 3: Extend `CanonicalLandmarkTypes`

**Files:**
- Modify: `src/Mithril.MapCalibration/CanonicalLandmarkTypes.cs`

- [ ] **Step 1: Add the two pin constants, the `PinTypes` set, and append to `All`**

```csharp
public const string MapPinCircle = "MapPinCircle";
public const string MapPinSquare = "MapPinSquare";

public static readonly IReadOnlySet<string> PinTypes =
    new HashSet<string>(StringComparer.Ordinal)
    { MapPinCircle, MapPinSquare };

public static readonly IReadOnlySet<string> All =
    new HashSet<string>(StringComparer.Ordinal)
    { Portal, MeditationPillar, TeleportationPlatform, Npc, MapPinCircle, MapPinSquare };
```

- [ ] **Step 2: Update the XML doc comments on `LandmarkTypes` and `PinTypes` to reference each other** (so the next reader sees both allowlists side-by-side).

### Task 4: Unit test the vocabulary additions

**Files:**
- Modify (or new): `tests/Mithril.MapCalibration.Tests/CanonicalLandmarkTypesTests.cs`

- [ ] **Step 1: Tests verify `PinTypes` contains exactly `MapPinCircle` + `MapPinSquare`; `All` is a strict superset of `LandmarkTypes ∪ PinTypes ∪ {Npc}`; ordinal-string comparer is in use on both sets**

```csharp
[Fact]
public void PinTypes_contains_both_pin_constants()
    => CanonicalLandmarkTypes.PinTypes.Should()
        .BeEquivalentTo(new[] { CanonicalLandmarkTypes.MapPinCircle, CanonicalLandmarkTypes.MapPinSquare });

[Fact]
public void All_is_superset_of_landmark_npc_and_pin_types()
{
    CanonicalLandmarkTypes.All.Should().Contain(CanonicalLandmarkTypes.LandmarkTypes);
    CanonicalLandmarkTypes.All.Should().Contain(CanonicalLandmarkTypes.PinTypes);
    CanonicalLandmarkTypes.All.Should().Contain(CanonicalLandmarkTypes.Npc);
}
```

- [ ] **Step 2: `dotnet test tests/Mithril.MapCalibration.Tests` passes.**

---

## Phase 3 — Asset extraction

### Task 5: Widen `IconTemplateExtractor` allowlist

**Files:**
- Modify: `tools/Mithril.MapCalibration.Tools.Common/IconTemplateExtractor.cs`

- [ ] **Step 1: Add the two pin entries to `LandmarkIcons`** (append after `landmark_npc`; preserve existing entries and the `landmark_star` rationale comment)

```csharp
private static readonly (string TextureName, string LandmarkType)[] LandmarkIcons =
[
    ("landmark_telepad", CanonicalLandmarkTypes.TeleportationPlatform),
    ("landmark_medipillar", CanonicalLandmarkTypes.MeditationPillar),
    ("landmark_portal", CanonicalLandmarkTypes.Portal),
    ("landmark_npc", CanonicalLandmarkTypes.Npc),
    // landmark_star is generic waypoint; skip — no Type to match on.
    // User-placed map pin sprites — hollow outline shapes, pivot centred (verified
    // 2026-06-03 against live sharedassets0.assets). Color tint is applied at
    // runtime; NCC is luma-normalized so it's a no-op for matching.
    ("MapPin_Circle", CanonicalLandmarkTypes.MapPinCircle),
    ("MapPin_Square", CanonicalLandmarkTypes.MapPinSquare),
];
```

- [ ] **Step 2: Bump `CacheFormatVersion` to `4`** (so any user's existing 4-icon cache re-extracts to 6 icons on next launch).

### Task 6: Re-extract bundled icon templates

- [ ] **Step 1: Run the sidecar against the live PG install**

```bash
dotnet run --project tools/Mithril.AssetExtractor -c Release -- \
  --install "C:\Program Files (x86)\Steam\steamapps\common\Project Gorgon" \
  --out "%LocalAppData%\Mithril\assets" --icons \
  --tpk "%LocalAppData%\Mithril\assets\classdata.tpk"
```

- [ ] **Step 2: Confirm the sidecar emits `6 icons extracted` (or equivalent count line) and the `MapPin_Circle` + `MapPin_Square` lines appear**.
- [ ] **Step 3: Capture the new `pixelSha256` from the sidecar's stdout JSON.**
- [ ] **Step 4: Copy the regenerated `icon-templates.json` + `icon-templates.bin` from the cache dir to `src/Mithril.MapCalibration.Detection/BundledData/`** (replacing the existing 4-icon blobs).

### Task 7: Update `CanonicalAssetHashes`

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashes.cs`

- [ ] **Step 1: Add the new `pixelSha256` from Task 6 Step 3 as the authoritative icon-template manifest hash. Retire the previous 4-icon hash** (replace rather than back-compat alias; the bundled blob ships only the new shape).
- [ ] **Step 2: Build green: `dotnet build Mithril.slnx`.**
- [ ] **Step 3: Smoke-load the detection layer to verify the gate accepts the new blob.** Existing bundled-data load tests (e.g. `BundledIconTemplateLoaderTests`) must still pass — they now load 6 icons.

---

## Phase 4 — Reference provider seam

### Task 8: Add `IAreaReferenceProviderSource` marker

**Files:**
- New: `src/Mithril.MapCalibration.Capture/IAreaReferenceProviderSource.cs`

- [ ] **Step 1: Define the marker interface**

```csharp
namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Identifies a contributor to <see cref="CompositeAreaReferenceProvider"/>.
/// Separate from <see cref="IAreaReferenceProvider"/> (the consumer-facing
/// composite) so the composite's DI registration doesn't recursively resolve
/// itself.
/// </summary>
public interface IAreaReferenceProviderSource
{
    IReadOnlyList<LandmarkReference> ForArea(MapSceneRef scene);
}
```

> **#1021 seam note.** This signature uses `MapSceneRef` per #1021's D4 ratified design. If #1021's spec is in main but its implementation PR has not merged at this PR's land time, this PR includes a small `MapSceneRef` shim record (parent area + optional scene friendly name) in `Mithril.MapCalibration` to keep types resolving. The shim is type-compatible with #1021's final definition; merge order does not require redesign.

### Task 9: Implement `MapPinAreaReferenceProvider`

**Files:**
- New: `src/Mithril.MapCalibration.Capture/MapPinAreaReferenceProvider.cs`

- [ ] **Step 1: Implement the provider per spec §5.3** (Shape switch, `MapPinDescriptor.Describe` for `Name`, `ThrottledWarn` on unmapped shape).

```csharp
public sealed class MapPinAreaReferenceProvider : IAreaReferenceProviderSource
{
    private readonly IMapPinState _pinState;
    private readonly ILogger? _logger;

    public MapPinAreaReferenceProvider(IMapPinState pinState, ILogger? logger = null)
    {
        _pinState = pinState;
        _logger = logger;
    }

    public IReadOnlyList<LandmarkReference> ForArea(MapSceneRef scene)
    {
        var pins = _pinState.Pins;
        if (pins.Count == 0) return Array.Empty<LandmarkReference>();

        var refs = new List<LandmarkReference>(pins.Count);
        var unmapped = 0;
        foreach (var pin in pins)
        {
            var type = pin.Shape switch
            {
                0 => CanonicalLandmarkTypes.MapPinCircle,
                1 => CanonicalLandmarkTypes.MapPinSquare,
                _ => null,
            };
            if (type is null) { unmapped++; continue; }

            refs.Add(new LandmarkReference(
                Type: type,
                Name: MapPinDescriptor.Describe(pin),
                World: new WorldCoord(pin.X, 0, pin.Z)));
        }

        if (unmapped > 0)
        {
            ThrottledWarn.Log(_logger,
                $"Dropped {unmapped} map pin(s) with unmapped Shape int (only 0 and 1 are documented).");
        }
        return refs;
    }
}
```

### Task 10: Implement `CompositeAreaReferenceProvider` + refit existing provider

**Files:**
- New: `src/Mithril.MapCalibration.Capture/CompositeAreaReferenceProvider.cs`
- Modify: `src/Mithril.MapCalibration.Capture/ReferenceDataAreaReferenceProvider.cs`

- [ ] **Step 1: Add the composite per spec §5.4**

```csharp
public sealed class CompositeAreaReferenceProvider : IAreaReferenceProvider
{
    private readonly IReadOnlyList<IAreaReferenceProviderSource> _sources;

    public CompositeAreaReferenceProvider(IEnumerable<IAreaReferenceProviderSource> sources)
    {
        _sources = sources.ToArray();
    }

    public IReadOnlyList<LandmarkReference> ForArea(MapSceneRef scene)
    {
        var result = new List<LandmarkReference>();
        foreach (var src in _sources) result.AddRange(src.ForArea(scene));
        return result;
    }
}
```

- [ ] **Step 2: Refit `ReferenceDataAreaReferenceProvider`** to implement `IAreaReferenceProviderSource` instead of `IAreaReferenceProvider`. The method body changes only in signature (`string areaKey` → `MapSceneRef scene`); the implementation reads `scene.ParentAreaKey` for the existing landmarks/NPCs lookup. Sub-zone filter (`SceneFriendlyName`) is honoured per #1021's spec D4 — when set, NPCs additionally filter on `AreaFriendlyName == sceneFriendly`.

### Task 11: DI wireup

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`

- [ ] **Step 1: Replace the existing single `IAreaReferenceProvider` registration with the multi-bind pattern**

```csharp
services.AddSingleton<IAreaReferenceProviderSource, ReferenceDataAreaReferenceProvider>();
services.AddSingleton<IAreaReferenceProviderSource, MapPinAreaReferenceProvider>();
services.AddSingleton<IAreaReferenceProvider, CompositeAreaReferenceProvider>();
```

- [ ] **Step 2: Build green: `dotnet build Mithril.slnx`.**
- [ ] **Step 3: Run existing capture-tier tests** to confirm the new DI shape doesn't break consumers: `dotnet test tests/Mithril.MapCalibration.Capture.Tests`.

### 🔍 Review checkpoint A — provider seam complete

**Trigger `superpowers:requesting-code-review` now.**

Why here, not earlier or later:
- The seam shape (`MapSceneRef`-typed `ForArea`, marker-interface split, composite registration) is the load-bearing architectural decision. Downstream phases (engine cold-start hint, integration tests) only consume this shape; if it's wrong, the rework hits a much smaller blast radius now than after Phase 6.
- All Phase 1–4 code can be exercised in isolation (unit tests; manual DI resolution). The reviewer has enough to validate without seeing the engine wiring.

What to ask the reviewer to focus on:
- `IAreaReferenceProviderSource` vs `IAreaReferenceProvider` split — is this the right way to prevent DI cycle, or is there a lighter idiom?
- `MapPinAreaReferenceProvider`'s `MapSceneRef` ignoring `SceneFriendlyName` — verification-owed (per spec §8 / D9) but should be flagged explicitly in code comments.
- `MapPinDescriptor` placement in `Arda.Contracts` vs a new `Mithril.GameState.Pins` project (per `docs/player-pin-service.md` line 1 anticipated naming).

Address review feedback inline (no separate phase); resume at Phase 5 when comments resolve.

---

## Phase 5 — Engine cold-start hint

### Task 12: Add `OutcomeVocabulary.RejectedNeedsMorePins`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs`

- [ ] **Step 1: Add the constant** (alphabetical order with existing `Rejected*` constants)

```csharp
public const string RejectedNeedsMorePins = "rejected-needs-more-pins";
```

### Task 13: Wire the cold-start hint in `AutoCalibrationEngine`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`

- [ ] **Step 1: After the existing `var references = _references.ForArea(scene);` + the existing reference-count log line, BEFORE `EnsureIconTemplatesAsync`, add the predicate check** (per spec §5.5)

```csharp
const int PinFloorRefs = 3;
if (references.Count < PinFloorRefs
    && references.All(r => CanonicalLandmarkTypes.PinTypes.Contains(r.Type)))
{
    attempt.Outcome = OutcomeVocabulary.RejectedNeedsMorePins;
    return Fail(
        scene,
        "drop ≥3 map pins at well-spread spots to enable auto-calibration for this area",
        OutcomeVocabulary.RejectedNeedsMorePins);
}
```

- [ ] **Step 2: Update `CalibrationStatusFormatter`** with the user-facing string

```csharp
OutcomeVocabulary.RejectedNeedsMorePins =>
    "Drop ≥3 map pins at well-spread spots to enable auto-calibration for this area.",
```

### Task 14: Unit tests for the cold-start hint

**Files:**
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` (extend existing file if present; else create alongside)

- [ ] **Step 1: Tests covering the four cases per spec §6.1**

```csharp
[Fact]
public async Task RunAttempt_pin_only_refs_below_floor_returns_needs_more_pins() { ... }

[Fact]
public async Task RunAttempt_mixed_refs_below_floor_falls_through() { ... }

[Fact]
public async Task RunAttempt_pin_only_refs_at_or_above_floor_falls_through() { ... }

[Fact]
public async Task RunAttempt_landmark_only_refs_below_floor_falls_through_unchanged() { ... }
```

Each uses a fake `IAreaReferenceProvider` returning a controlled list; assert on `AutoCalibrationOutcome.OutcomeCategory` (and the `Fail`/fall-through observable side effect for the proceeded path).

- [ ] **Step 2: `dotnet test tests/Mithril.MapCalibration.Capture.Tests` passes.**

---

## Phase 6 — Integration + regression tests

### Task 15: End-to-end synthetic-frame integration tests

**Files:**
- Modify (or new): `tests/Mithril.MapCalibration.Detection.Tests/MapCalibrationSolveEngineTests.cs` (or the analogous integration project)

- [ ] **Step 1: Synthesize a fake screenshot with N pin detections** (use `ImageOps`/`GrayImage` directly to draw a 16×16 white ring at known pixel coords) **+ matching pin refs at well-spread world coords**.

- [ ] **Step 2: Verify the full pipeline (`DetectionRequest → Solve`) produces a calibration with residual < gate threshold; the inlier set contains the pin types**.

- [ ] **Step 3: Add the clustered-degenerate test** (3 detections within 50 px → existing 100-px bbox guard rejects).

- [ ] **Step 4: Add the mixed-types test** (2 landmarks + 3 pins → inlier set contains both type prefixes).

- [ ] **Step 5: Add the shape-mismatch test** (`MapPinCircle` detection, only `MapPinSquare` refs → detection contributes no inliers).

### Task 16: Landmark-only byte-parity regression test

**Files:**
- Modify (or new): `tests/Mithril.MapCalibration.Capture.Tests/LandmarkPathRegressionTests.cs`

- [ ] **Step 1: Replay the existing `AreaEltibule-…-accepted` bundle inputs through the new composite-provider DI graph**.

- [ ] **Step 2: Assert outcome category, residual (to 4 decimal places), inlier count, persisted `AreaCalibration` byte-identity vs. the recorded accepted bundle** — the no-pin path must stay equivalent to today's behaviour.

### Task 17: Gated sidecar extraction test

**Files:**
- Modify (or new): `tests/Mithril.MapCalibration.Tools.Tests/IconTemplateExtractorTests.cs`

- [ ] **Step 1: Skip-gate the test if PG install isn't detectable** (existing pattern in the tools test project).

- [ ] **Step 2: Run extraction; verify `MapPin_Circle` and `MapPin_Square` entries land with the matching `LandmarkType` strings; verify the bundled `pixelSha256` matches `CanonicalAssetHashes`**.

---

## Phase 7 — Manual verification + INDEX + PR

### Task 18: Manual verification captures

> Owner: @arthur-conde. Captures attach to the PR's test-plan checklist.

- [ ] **Step 1: Build + launch the shell from the worktree**

```bash
dotnet build src/Mithril.Shell
dotnet run --project src/Mithril.Shell
```

- [ ] **Step 2: In `AreaEltibule` (landmark-rich), zero pins**: hit the calibrate hotkey → outcome unchanged vs. main; bundle's `04-references.json` lists only landmark/NPC refs. Attach the bundle path + outcome to the PR.
- [ ] **Step 3: Same area, drop 3 pins at well-spread spots, re-calibrate**: outcome accepted; bundle shows mixed landmark + pin refs; residual marginally lower than Step 2. Attach.
- [ ] **Step 4: Enter `AreaCave1` (Dungeons Beneath Eltibule, Hogan's basement)**: with 0 pins, re-calibrate → outcome `RejectedNeedsMorePins`; status string reads the new hint. Attach.
- [ ] **Step 5: Same area, drop 3 well-spread pins, re-calibrate**: outcome accepted; visually verify the overlay's projection lands on real in-game landmarks. Attach.
- [ ] **Step 6: Cross-reference with the pin arg-A investigation** (sibling issue). If that issue has reported arg-A semantics, note interaction in the PR description.

### Task 19: Flip INDEX.md status

**Files:**
- Modify: `docs/planning/INDEX.md`

- [ ] **Step 1: Append the new row** (already added in this branch as `active`; flip to `shipped` at merge time per the standard convention — the PR's last commit before merge)

### 🔍 Review checkpoint B — pre-PR final review

**Trigger `superpowers:requesting-code-review` now.**

Scope: the whole branch (all phases). The reviewer sees the complete feature in context, with manual-verification captures already attached.

What to ask the reviewer to focus on:
- Cold-start hint placement (after refs, before sidecar) — does the early-return ordering match the engine's other reject paths?
- Pin-typing per-shape decision — does the actual NCC discriminability in the live captures (Tasks 18 steps 3 + 5) match the spec's expectations? If a `MapPin*`-specific NCC threshold override is needed (per spec §8), this PR may need to land it.
- Composite provider edge cases — empty source list, one source throws, source order stability.

Address feedback; re-run `dotnet build`, `dotnet test`, and a quick spot-check of the headline `AreaCave1` capture. Resume at Task 20 when comments resolve.

### Task 20: Open the PR

- [ ] **Step 1: Push the branch + open the PR with `gh pr create`**, body links to spec + plan + INDEX row + the manual-verification captures.

```bash
gh pr create --title "feat(map-calibration): use map pins as auto-calibration anchors" --body "$(cat <<'EOF'
Closes #1036.

## Summary
- Adds map pins as a second auto-calibration anchor source so landmark-free areas (dungeons, instanced sub-zones) can be calibrated without manual click-pair flow.
- Two new icon templates (`MapPin_Circle`, `MapPin_Square`), two new `CanonicalLandmarkTypes` constants, one new `MapPinAreaReferenceProvider`, one new `CompositeAreaReferenceProvider`, one new `OutcomeVocabulary.RejectedNeedsMorePins`, one new shared `MapPinDescriptor` helper.
- See [`docs/planning/map-calibration-pin-anchor-v1/spec.md`](https://github.com/moumantai-gg/mithril/blob/main/docs/planning/map-calibration-pin-anchor-v1/spec.md) for ratified design decisions and architecture; [`plan.md`](https://github.com/moumantai-gg/mithril/blob/main/docs/planning/map-calibration-pin-anchor-v1/plan.md) for the phased implementation walk.

## Test plan
- [ ] `dotnet build Mithril.slnx` green.
- [ ] `dotnet test Mithril.slnx` green.
- [ ] Manual: AreaEltibule no-pin → unchanged outcome.
- [ ] Manual: AreaEltibule + 3 pins → accepted, mixed inliers.
- [ ] Manual: AreaCave1 no-pin → RejectedNeedsMorePins.
- [ ] Manual: AreaCave1 + 3 pins → accepted, projection visually correct.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 2: After PR merges, flip INDEX.md status `active` → `shipped`** in a follow-up tiny PR (or as part of the same squash, per house convention).

---

## Self-review checklist (run before declaring the plan complete)

- [ ] Every spec section §1–§8 has a corresponding phase or task footprint here (no spec section is unaddressed).
- [ ] Phases land in build-green order — no task references a type that hasn't been added by an earlier task.
- [ ] No task block has more than one mechanical edit (each step is independently reviewable).
- [ ] The two review checkpoints are at shape-lock moments (post-seam, pre-PR), not after every task.
- [ ] Sidecar re-extraction (Task 6) instructions match the actually-runnable command shape — the user's local `classdata.tpk` path is stable across sessions.
- [ ] Manual-verification steps (Task 18) cover the headline `AreaCave1` case that motivates the work.
- [ ] No "TBD" / "TODO" / "decide later" markers in the plan body. Decisions live in the spec.
- [ ] The plan does not duplicate the spec's design rationale. Rationale references go to the spec; the plan is the build sequence.
