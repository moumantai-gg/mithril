# Implementation plan — calibration-1005-scale-aware-gate

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development with `model: sonnet` for implementer + reviewer subagents. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the monotonicity gate from rejecting a re-capture taken at a different in-game zoom, and stop the chip from telling the user to do the action that just tripped the gate. See `spec.md` in this folder for the design.

**Architecture:** Three additive edits inside `Mithril.MapCalibration` and `Mithril.MapCalibration.Capture`:
1. `AreaCalibration` gains a nullable `LocatorScale` (additive — no schema bump; relies on `JsonIgnoreCondition.WhenWritingDefault` + source-gen ignoring unknown properties on read).
2. `AutoCalibrationEngine` stamps the candidate's `LocatorScale` from `refineResult.Metrics?.Scale` and wraps `CheckMonotonicAccept` in an `IsSameScaleRegime(existing, candidate)` predicate that returns false (skip the gate, accept) whenever either side is null/non-finite/≤0 OR the two factors differ by more than 2% relative.
3. `AutoCalibrationOutcome` gains a nullable `OutcomeCategory`; `CalibrationStatusFormatter.ForOutcome` routes structurally on it first and falls back to the existing substring path in `ForReject` when null. `RejectedNotMonotonic` gets its own user-facing message.

Storage stays single-slot per area; per-scale storage is the follow-up at [#1006](https://github.com/moumantai-gg/mithril/issues/1006).

**Tech Stack:** .NET 10, `System.Text.Json` source-generated context (`MapCalibrationJsonContext`), xunit + FluentAssertions.

**Reference files (read before starting):**
- `src/Mithril.MapCalibration/AreaCalibration.cs` — the record being extended
- `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs` — JSON source-gen options (note `DefaultIgnoreCondition = WhenWritingDefault`)
- `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs:132-161` — `MathEquals` projection comparison (must NOT include `LocatorScale` — it's metadata, not transform)
- `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs:396-435, 568-599` — accept path + gate + monotonicity helper
- `src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs` — produces `LocateMetrics.Scale`
- `src/Mithril.MapCalibration.Capture/LocateMetrics.cs` — the record with `Scale` etc.
- `src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs` — the refiner's return type
- `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs` — the chip-message router
- `src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs` — outcome category constants (including `RejectedNotMonotonic`)
- `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs` + `AutoCalibrationEngineTests.cs` — fake/harness patterns for engine tests

---

## Task 1: Add `LocatorScale` to `AreaCalibration` + JSON round-trip

**Files:**
- Modify: `src/Mithril.MapCalibration/AreaCalibration.cs` (the record at line 21)
- Modify: `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs` (the `MathEquals` doc comment at line 132-138 — extend the "metadata, not transform" list)
- Test: `tests/Mithril.MapCalibration.Tests/AreaCalibrationRoundTripTests.cs` (extend the existing file)

- [ ] **Step 1: Write the failing JSON round-trip tests**

In `tests/Mithril.MapCalibration.Tests/AreaCalibrationRoundTripTests.cs`, add:

```csharp
[Fact]
public void Roundtrip_preserves_LocatorScale()
{
    var original = new AreaCalibration(
        Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
        ReferenceCount: 5, ResidualPixels: 0.5)
    {
        LocatorScale = 0.408,
    };

    var json = JsonSerializer.Serialize(original, MapCalibrationJsonContext.Default.AreaCalibration);
    var rt = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration);

    rt.Should().NotBeNull();
    rt!.LocatorScale.Should().Be(0.408);
}

[Fact]
public void Roundtrip_omits_LocatorScale_from_JSON_when_null()
{
    var original = new AreaCalibration(1.0, 0.0, 0.0, 0.0, 5, 0.5);

    var json = JsonSerializer.Serialize(original, MapCalibrationJsonContext.Default.AreaCalibration);

    // DefaultIgnoreCondition = WhenWritingDefault means null nullable doubles
    // are omitted. This is the downgrade-safety guarantee: an old build
    // deserialising the new shape sees a property it doesn't know about and
    // ignores it; a new build deserialising the old shape gets null on the
    // missing property.
    json.Should().NotContain("locatorScale", "null nullable doubles should be omitted");

    var rt = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration);
    rt!.LocatorScale.Should().BeNull();
}

[Fact]
public void Roundtrip_accepts_legacy_JSON_without_LocatorScale_field()
{
    // Synthesise a "pre-1005" JSON payload — no locatorScale field.
    const string legacyJson = """
        {"scale":1.0,"rotationRadians":0.0,"originX":0.0,"originY":0.0,
         "referenceCount":5,"residualPixels":0.5}
        """;

    var rt = JsonSerializer.Deserialize(legacyJson, MapCalibrationJsonContext.Default.AreaCalibration);

    rt.Should().NotBeNull();
    rt!.LocatorScale.Should().BeNull();
}
```

Imports needed at the top of the file (add only the ones not already present):

```csharp
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Internal;
using Xunit;
```

- [ ] **Step 2: Run the new tests to verify they fail**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~LocatorScale"
```

Expected: compile error — `'AreaCalibration' does not contain a definition for 'LocatorScale'`.

- [ ] **Step 3: Add the property to `AreaCalibration`**

In `src/Mithril.MapCalibration/AreaCalibration.cs`, immediately after the `SchemaVersion` property (around line 60), add:

```csharp
/// <summary>
/// The texture&#8594;screenshot scale the
/// <see cref="Capture.FeatureMatchingRefiner"/> RANSAC-recovered when this
/// calibration was solved &#8212; the <see cref="Capture.LocateMetrics.Scale"/>
/// of the locator's partial-affine fit. Intrinsic to the capture: larger =
/// more zoomed in (texture pixels expanded into the captured frame), smaller
/// = more zoomed out. The <see cref="Capture.AutoCalibrationEngine"/>
/// monotonicity gate compares this between a new fit and the stored one and
/// skips the quality comparison when the two regimes differ &#8212; pixel
/// residual and inlier count are not commensurable across zoom regimes
/// (see #1005).
///
/// <para><b>Additive:</b> nullable, defaults to <see langword="null"/>.
/// Records written by pre-#1005 builds load with <see langword="null"/>; the
/// gate treats null as "regime unknown &#8594; skip the gate". No
/// <see cref="SchemaVersion"/> bump &#8212; per the <c>CalibrationSource</c>
/// precedent (additive property, downgraded builds ignore unknown JSON,
/// upgraded builds default missing property to null).</para>
/// </summary>
public double? LocatorScale { get; init; }
```

- [ ] **Step 4: Update `MathEquals` doc comment to call out the new metadata field**

In `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs` at line 132-138, replace the doc comment block with:

```csharp
/// <summary>
/// Compares only the fields that determine the world&#8596;pixel
/// projection. Ignores <see cref="AreaCalibration.Source"/> (always
/// re-stamped on import), <see cref="AreaCalibration.SchemaVersion"/>,
/// <see cref="AreaCalibration.ReferenceCount"/>,
/// <see cref="AreaCalibration.ResidualPixels"/>, and
/// <see cref="AreaCalibration.LocatorScale"/> (all metadata, not transform).
///
/// <para>Uses a relative tolerance instead of raw <c>==</c> so a one-ULP
/// drift from JSON round-trip / cross-JIT codegen does not re-trigger an
/// overwrite (and an unnecessary disk write) on every startup. The
/// tolerance is tighter than any value the calibration math can produce
/// in practice (scale ~1, rotation ~3, origin up to ~2000), so a real
/// recalibration is never mistaken for "already in sync".</para>
/// </summary>
```

The `MathEquals` *body* requires no change — it only compares projection fields and naturally ignores `LocatorScale`. The doc comment update keeps the inventory of intentionally-ignored fields complete for future readers.

- [ ] **Step 5: Run the new tests to verify they pass**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~LocatorScale"
```

Expected: PASS (3 tests).

- [ ] **Step 6: Run the broader Mithril.MapCalibration test project to confirm nothing else broke**

```bash
dotnet test tests/Mithril.MapCalibration.Tests
```

Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/Mithril.MapCalibration/AreaCalibration.cs \
        src/Mithril.MapCalibration/Internal/UserRefinementStore.cs \
        tests/Mithril.MapCalibration.Tests/AreaCalibrationRoundTripTests.cs
git commit -m "feat(map-calibration): add AreaCalibration.LocatorScale (#1005)

Additive nullable property stamping the texture->screenshot scale the
FeatureMatchingRefiner converged on for this calibration. No schema-version
bump: relies on WhenWritingDefault + source-gen ignoring unknown properties
for symmetric downgrade/upgrade safety, per the CalibrationSource precedent.

Round-trip tests cover: value preserved, null omitted from JSON, pre-#1005
JSON deserialises with null. Touched MathEquals doc comment to record the
new field as metadata-ignored (no body change — projection-field list is
positive, not negative)."
```

---

## Task 2: Add `OutcomeCategory` to `AutoCalibrationOutcome`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` (the `AutoCalibrationOutcome` record at the end of the file)
- Test: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationOutcomeTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationOutcomeTests.cs`:

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class AutoCalibrationOutcomeTests
{
    [Fact]
    public void OutcomeCategory_defaults_to_null_for_legacy_callers()
    {
        // Three-positional construction must still compile — callers that
        // haven't been updated keep working with OutcomeCategory = null,
        // and CalibrationStatusFormatter falls back to its substring path.
        var outcome = new AutoCalibrationOutcome(Persisted: false, AreaKey: "AreaTest", RejectReason: "x");

        outcome.OutcomeCategory.Should().BeNull();
    }

    [Fact]
    public void OutcomeCategory_carries_through_when_set()
    {
        var outcome = new AutoCalibrationOutcome(
            Persisted: false,
            AreaKey: "AreaTest",
            RejectReason: "x",
            OutcomeCategory: OutcomeVocabulary.RejectedNotMonotonic);

        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNotMonotonic);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationOutcomeTests"
```

Expected: compile error — `'AutoCalibrationOutcome' does not contain a definition for 'OutcomeCategory'`.

- [ ] **Step 3: Add the field to the record**

At the bottom of `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`, replace:

```csharp
public sealed record AutoCalibrationOutcome(bool Persisted, string AreaKey, string? RejectReason);
```

with:

```csharp
/// <summary>
/// The outcome of one auto-calibration attempt: whether a transform was
/// persisted, the area it was for, a user-facing reason when not persisted
/// (<see cref="CalibrationStatusFormatter"/>), and the structured outcome
/// category (one of the constants on <see cref="Diagnostics.OutcomeVocabulary"/>).
///
/// <para><see cref="OutcomeCategory"/> is nullable for backward-compat with
/// callers that pre-date #1005; <see cref="CalibrationStatusFormatter.ForOutcome"/>
/// routes structurally when it is set and falls back to substring-matching
/// the <see cref="RejectReason"/> when null. New engine return sites MUST
/// populate it.</para>
/// </summary>
public sealed record AutoCalibrationOutcome(
    bool Persisted,
    string AreaKey,
    string? RejectReason,
    string? OutcomeCategory = null);
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationOutcomeTests"
```

Expected: PASS (2 tests).

- [ ] **Step 5: Build the rest of the solution to confirm existing callers still compile**

```bash
dotnet build Mithril.slnx
```

Expected: green. Optional-parameter addition is non-breaking; positional construction at existing sites continues to compile.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs \
        tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationOutcomeTests.cs
git commit -m "feat(map-calibration): add AutoCalibrationOutcome.OutcomeCategory (#1005)

Nullable optional positional. Default null preserves every existing call site.
Carries OutcomeVocabulary constants so CalibrationStatusFormatter can route
structurally instead of substring-matching the RejectReason."
```

---

## Task 3: `IsSameScaleRegime` helper + tolerance/null unit tests

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` (add static helper alongside `CheckMonotonicAccept`)
- Test: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineScaleRegimeTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineScaleRegimeTests.cs`:

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class AutoCalibrationEngineScaleRegimeTests
{
    [Theory]
    [InlineData(0.408, 0.408)]      // identical
    [InlineData(0.408, 0.416)]      // +1.96% — inside ±2%
    [InlineData(0.408, 0.400)]      // -1.96%
    [InlineData(1.250, 1.250)]
    public void Same_regime_when_factors_within_2_percent(double existing, double candidate)
    {
        AutoCalibrationEngine.IsSameScaleRegime(existing, candidate).Should().BeTrue();
    }

    [Theory]
    [InlineData(0.408, 0.420)]      // +2.94% — outside
    [InlineData(0.408, 0.395)]      // -3.19%
    [InlineData(0.408, 0.800)]      // wildly different
    [InlineData(0.200, 1.500)]      // far apart
    public void Different_regime_when_factors_differ_more_than_2_percent(double existing, double candidate)
    {
        AutoCalibrationEngine.IsSameScaleRegime(existing, candidate).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 0.408)]
    [InlineData(0.408, null)]
    [InlineData(null, null)]
    public void Null_on_either_side_skips_the_gate(double? existing, double? candidate)
    {
        // "Regime unknown" → return false → gate skipped → accept unconditionally.
        // This is the legacy-record escape hatch: a pre-#1005 stored cal stamped
        // null cannot block a new capture forever.
        AutoCalibrationEngine.IsSameScaleRegime(existing, candidate).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Degenerate_value_on_either_side_skips_the_gate(double bad)
    {
        // Defensive: a non-positive/non-finite stored factor can't anchor a ratio
        // comparison. Treat as regime-unknown rather than throwing or asserting.
        AutoCalibrationEngine.IsSameScaleRegime(bad, 0.408).Should().BeFalse();
        AutoCalibrationEngine.IsSameScaleRegime(0.408, bad).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngineScaleRegimeTests"
```

Expected: compile error — `'AutoCalibrationEngine' does not contain a definition for 'IsSameScaleRegime'`.

- [ ] **Step 3: Implement the helper**

In `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`, immediately above `CheckMonotonicAccept`, add:

```csharp
/// <summary>
/// True when an existing stored calibration and a new candidate were both
/// solved at the same in-game zoom regime &#8212; i.e. the locator's
/// <see cref="LocateMetrics.Scale"/> values agree within
/// <see cref="ScaleRegimeRelTolerance"/> (currently 2%, generous over the
/// FeatureMatchingRefiner's sub-percent stability for repeated captures at
/// the same zoom).
///
/// <para>Returns <see langword="false"/> when either side is <see langword="null"/>
/// (legacy record stamped pre-#1005, or a candidate whose locator didn't
/// populate the factor) OR non-positive/non-finite. "Regime unknown" routes
/// to "skip the gate, accept the new fit" at the call site &#8212; the
/// monotonicity check is only valid when both fits saw the same icon-size
/// regime, and we have no basis to claim that when the data is missing or
/// degenerate.</para>
/// </summary>
private const double ScaleRegimeRelTolerance = 0.02;

internal static bool IsSameScaleRegime(double? existing, double? candidate)
{
    if (existing is not { } e || candidate is not { } c) return false;
    if (!double.IsFinite(e) || !double.IsFinite(c) || e <= 0 || c <= 0) return false;
    return Math.Abs(c / e - 1.0) <= ScaleRegimeRelTolerance;
}
```

Keep the existing `MonotonicResidualRatio` / `MonotonicInlierDelta` constants in place — they're still consumed by `CheckMonotonicAccept`.

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngineScaleRegimeTests"
```

Expected: PASS (14 tests across the four theory groups).

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs \
        tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineScaleRegimeTests.cs
git commit -m "feat(map-calibration): add IsSameScaleRegime helper (#1005)

Pure predicate: existing+candidate LocatorScale within 2% relative →
same regime. Any null or degenerate value → false (regime unknown). Wired
into the engine in the next commit; this lands the helper with its own
test surface so the wiring change is small."
```

---

## Task 4: Stamp `LocatorScale` on the candidate + wrap monotonicity check in `IsSameScaleRegime`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` (the accept path around line 396-435)
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs` (`FakeRefiner` may need a `LocateMetrics` parameter — see below)
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` (add new tests; keep existing tests passing)

- [ ] **Step 1: Update `FakeRefiner` to carry optional `LocateMetrics`**

Look at the current `FakeRefiner` in `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs`. It returns a `MapRegionRefineResult` whose `Metrics` is currently always null in the legacy harness. Extend it so tests can pass a custom `LocateMetrics`:

```csharp
internal sealed class FakeRefiner : IMapRegionRefiner
{
    private readonly MapRect? _rect;
    private readonly LocateMetrics? _metrics;
    public FakeRefiner(MapRect? rect, LocateMetrics? metrics = null)
    {
        _rect = rect;
        _metrics = metrics;
    }
    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture) =>
        new(AcceptedRect: _rect, RawFitRect: _rect, Metrics: _metrics);
}
```

(If `FakeRefiner` already takes a `MapRegionRefineResult` directly, the change is simpler — add a `LocateMetrics` parameter to whichever constructor the existing tests use. **Verify the current signature in the file before pasting the above** — the harness may have evolved.)

Add a small test-helper constant for building a "ordinary good fit" `LocateMetrics`:

```csharp
internal static class TestLocateMetrics
{
    /// <summary>A representative metrics block from a healthy locate. Tests
    /// that don't care about specific metric values use this so the harness
    /// stays succinct and the engine's gate logic sees a populated Metrics.</summary>
    public static LocateMetrics ForScale(double scale, int inlierCount = 50) =>
        new(InlierCount: inlierCount, CandidateCount: inlierCount * 2,
            InlierRatio: 0.5, Scale: scale, RotationDegrees: 0.0,
            Mirror: false, Tx: 0.0, Ty: 0.0, ResidualPixels: 1.0);
}
```

- [ ] **Step 2: Write the failing engine integration tests**

In `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs`, add:

```csharp
// ── #1005: scale-aware monotonicity gate ─────────────────────────────────

[Fact]
public async Task Persisted_calibration_carries_LocatorScale_from_the_locate_metrics()
{
    var svc = new FakeCalibrationService();
    var h = new EngineHarness
    {
        Solve = Accepted(residual: 0.65, inliers: 5),
        Service = svc,
        // Refiner returns a populated Metrics with a known scale — the
        // engine must stamp this onto the persisted AreaCalibration so the
        // gate has it to compare on the next attempt.
        Refiner = new FakeRefiner(
            new MapRect(0, 0, 64, 64, 64, 64),
            TestLocateMetrics.ForScale(0.408)),
    };

    var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

    outcome.Persisted.Should().BeTrue();
    svc.Saved[Area].LocatorScale.Should().Be(0.408);
}

[Fact]
public async Task Different_scale_regime_accepts_even_when_monotonicity_would_have_rejected()
{
    var svc = new FakeCalibrationService();
    // Seed an EXISTING calibration at scale 0.408 with high quality.
    svc.Seed(Area, SomeBaseline() with { LocatorScale = 0.408, ResidualPixels = 0.5, ReferenceCount = 10 });

    // Capture at scale 0.800 (different regime) with a WORSE-looking fit
    // (would trip both monotonicity arms: residual much higher, inliers much lower).
    // Different regime → gate skipped → accept.
    var h = new EngineHarness
    {
        Service = svc,
        Solve = Accepted(residual: 3.5, inliers: 4),
        Refiner = new FakeRefiner(
            new MapRect(0, 0, 64, 64, 64, 64),
            TestLocateMetrics.ForScale(0.800)),
    };

    var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

    outcome.Persisted.Should().BeTrue();
    outcome.RejectReason.Should().BeNull();
    svc.Saved[Area].LocatorScale.Should().Be(0.800);
}

[Fact]
public async Task Same_scale_regime_still_protects_a_good_fit_from_a_worse_one()
{
    // The original #988 protection: same in-game zoom, second wrong-fit
    // attempt seconds later. LocatorScale values match within tolerance,
    // gate fires, prior calibration kept.
    var svc = new FakeCalibrationService();
    svc.Seed(Area, SomeBaseline() with { LocatorScale = 0.408, ResidualPixels = 0.79, ReferenceCount = 10 });

    var h = new EngineHarness
    {
        Service = svc,
        Solve = Accepted(residual: 4.03, inliers: 4),
        Refiner = new FakeRefiner(
            new MapRect(0, 0, 64, 64, 64, 64),
            TestLocateMetrics.ForScale(0.411)), // within ±2%
    };

    var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

    outcome.Persisted.Should().BeFalse();
    outcome.RejectReason.Should().Contain("inlier"); // monotonicity-flavoured reason
    svc.Saved[Area].ResidualPixels.Should().Be(0.79); // prior preserved
}

[Fact]
public async Task Legacy_null_LocatorScale_on_existing_skips_the_gate()
{
    // Legacy record (pre-#1005) has null LocatorScale. A new capture's
    // candidate has a value. IsSameScaleRegime(null, _) → false → gate skipped.
    // First re-capture stamps a value and subsequent comparisons can gate normally.
    var svc = new FakeCalibrationService();
    svc.Seed(Area, SomeBaseline() with { LocatorScale = null, ResidualPixels = 0.5, ReferenceCount = 10 });

    var h = new EngineHarness
    {
        Service = svc,
        Solve = Accepted(residual: 5.0, inliers: 3), // would normally trip both gates
        Refiner = new FakeRefiner(
            new MapRect(0, 0, 64, 64, 64, 64),
            TestLocateMetrics.ForScale(0.408)),
    };

    var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

    outcome.Persisted.Should().BeTrue();
    svc.Saved[Area].LocatorScale.Should().Be(0.408); // legacy null replaced with stamped value
}
```

- [ ] **Step 3: Run the new tests to verify they fail in the expected way**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngineTests"
```

Expected:
- `Persisted_calibration_carries_LocatorScale_from_the_locate_metrics` fails because the engine doesn't stamp the factor yet.
- `Different_scale_regime_accepts…` fails because the gate still fires.
- `Same_scale_regime_still_protects…` should *pass* (the gate fires as today on the inlier-delta arm).
- `Legacy_null_LocatorScale_on_existing_skips_the_gate` fails because the gate still fires.

- [ ] **Step 4: Update the accept path to stamp and gate by regime**

In `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`, locate the accept path around line 396-435. Replace the existing block:

```csharp
// Gate-accept: persist through the user store stamped AutoCapture, which
// inherits user-store precedence by construction (Task 20).
var stamped = result.Calibration with { Source = CalibrationSource.AutoCapture };

// #988 monotonicity gate. When a stored calibration already exists for
// this area, the new fit must not regress residual/inlier quality (a
// wrong-fit second attempt that clears the cold-start gate would
// otherwise replace a good first attempt — see the Eltibule 03:11:05
// vs 03:11:30 pair in the originating issue). Cold start (no existing)
// takes the same accept path it always did.
var existing = _calibrationService.GetCalibration(area);
if (existing is not null)
{
    var monotonicReason = CheckMonotonicAccept(existing, stamped, result.InlierCount);
    if (monotonicReason is not null)
    {
        attempt.Outcome = OutcomeVocabulary.RejectedNotMonotonic;
        _logger?.LogInformation(
            "Auto-calibration rejected for {Area}: monotonicity gate — {Reason}. Prior calibration kept (residual {PriorResidual:0.00}px, refs {PriorRefs}).",
            area, monotonicReason, existing.ResidualPixels, existing.ReferenceCount);
        return new AutoCalibrationOutcome(Persisted: false, AreaKey: area, RejectReason: monotonicReason);
    }
}
```

with:

```csharp
// Gate-accept: persist through the user store stamped AutoCapture, which
// inherits user-store precedence by construction (Task 20). Stamp
// LocatorScale from the FeatureMatchingRefiner's recovered partial-affine
// scale (#1005) so the next attempt's regime comparison has an anchor.
var stamped = result.Calibration with
{
    Source = CalibrationSource.AutoCapture,
    LocatorScale = refineResult.Metrics?.Scale,
};

// #988 monotonicity gate, scale-aware (#1005). A new fit must not regress
// residual/inlier quality vs. an existing calibration at the SAME zoom
// regime — comparing across regimes is invalid because the per-attempt
// inlier count tracks visible-icon size, not fit quality (the
// RenderSizePx-16 typed-detection bar). When the regimes differ (or either
// side has no stamped factor — pre-#1005 legacy records, or a refiner
// returning null Metrics), skip the comparison and accept.
var existing = _calibrationService.GetCalibration(area);
if (existing is not null
    && IsSameScaleRegime(existing.LocatorScale, stamped.LocatorScale))
{
    var monotonicReason = CheckMonotonicAccept(existing, stamped, result.InlierCount);
    if (monotonicReason is not null)
    {
        attempt.Outcome = OutcomeVocabulary.RejectedNotMonotonic;
        _logger?.LogInformation(
            "Auto-calibration rejected for {Area}: monotonicity gate — {Reason}. Prior calibration kept (residual {PriorResidual:0.00}px, refs {PriorRefs}).",
            area, monotonicReason, existing.ResidualPixels, existing.ReferenceCount);
        return new AutoCalibrationOutcome(
            Persisted: false,
            AreaKey: area,
            RejectReason: monotonicReason,
            OutcomeCategory: OutcomeVocabulary.RejectedNotMonotonic);
    }
}
```

Note: the terminal accept return and `_calibrationService.SaveUserRefinement` call below stay unchanged for this task — `OutcomeCategory` propagation on the other return sites is Task 5.

- [ ] **Step 5: Verify the existing-test invariant holds**

Existing tests like `Persists_with_AutoCapture_source_on_accept` use `FakeRefiner` with null `Metrics` (or default-constructed). With the new gate logic:
- No existing calibration → cold-start accept (unchanged).
- Existing calibration + candidate with null `LocatorScale` → `IsSameScaleRegime` returns false → gate skipped → accept.

Both outcomes match the existing tests' assertions. Run the full engine suite:

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngineTests"
```

Expected: all green (existing + 4 new).

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs \
        tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs \
        tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs
git commit -m "fix(map-calibration): scale-aware monotonicity gate (#1005)

Stamp LocatorScale onto the persisted AreaCalibration from the
FeatureMatchingRefiner's recovered partial-affine scale (LocateMetrics.Scale),
and skip CheckMonotonicAccept when the existing stored regime differs from
the candidate's by more than 2% (or either side is null/degenerate).

The original #988 protection still fires on a same-regime re-capture
(Eltibule 03:11:05/30 pair). The zoom-out scenario from #1005 now lands
because different-regime comparisons are invalid for the inlier-count
arm — RenderSizePx-16 typed-detection count tracks visible-icon size,
not fit quality."
```

---

## Task 5: Populate `OutcomeCategory` on every engine return site

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` (every `return` in `RunAttemptCoreAsync` + `Fail`)
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/TestHelpers.cs` (extract `Accepted`/`Rejected`/`SomeBaseline` helpers if they're still private to `AutoCalibrationEngineTests`)
- Test: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineOutcomeCategoryTests.cs` (create)

- [ ] **Step 1: Extract test helpers (if needed)**

Open `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` and check whether `Accepted`, `Rejected`, `SomeBaseline` are still private. If they are, extract them to a new `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/TestHelpers.cs` (cut + paste, mark `internal static`, no logic change), then update `AutoCalibrationEngineTests` to call `TestHelpers.Accepted(…)` etc.

If they're already in `TestHelpers.cs` or otherwise shared, skip this step.

- [ ] **Step 2: Write the failing tests**

Create `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineOutcomeCategoryTests.cs`:

```csharp
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class AutoCalibrationEngineOutcomeCategoryTests
{
    private const string Area = "AreaEltibule";

    [Fact]
    public async Task Accept_outcome_carries_Accepted_category()
    {
        var h = new EngineHarness { Solve = TestHelpers.Accepted(0.5, 5) };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.Accepted);
    }

    [Fact]
    public async Task NoArea_outcome_carries_RejectedNoArea_category()
    {
        var h = new EngineHarness { CurrentArea = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNoArea);
    }

    [Fact]
    public async Task NoBbox_outcome_carries_RejectedNoBbox_category()
    {
        var h = new EngineHarness { Bbox = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNoBbox);
    }

    [Fact]
    public async Task PgNotForeground_outcome_carries_RejectedPgNotForeground_category()
    {
        var h = new EngineHarness { GameWindow = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedPgNotForeground);
    }

    [Fact]
    public async Task NoBaseTexture_outcome_carries_RejectedNoBaseTexture_category()
    {
        var h = new EngineHarness { BaseTexture = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNoBaseTexture);
    }

    [Fact]
    public async Task MapNotLocated_outcome_carries_RejectedMapNotLocated_category()
    {
        var h = new EngineHarness { Refiner = new FakeRefiner(null) };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedMapNotLocated);
    }

    [Fact]
    public async Task SolveReject_outcome_carries_a_Rejected_solve_subcategory()
    {
        var h = new EngineHarness { Solve = TestHelpers.Rejected("residual 25.00 px exceeds threshold 12.00 px") };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedSolveResidual);
    }

    [Fact]
    public async Task Monotonicity_reject_outcome_carries_RejectedNotMonotonic_category()
    {
        var svc = new FakeCalibrationService();
        svc.Seed(Area, TestHelpers.SomeBaseline() with { LocatorScale = 0.408, ResidualPixels = 0.79, ReferenceCount = 10 });

        var h = new EngineHarness
        {
            Service = svc,
            Solve = TestHelpers.Accepted(residual: 4.03, inliers: 4),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.411)),
        };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNotMonotonic);
    }
}
```

- [ ] **Step 3: Run to verify they fail**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngineOutcomeCategoryTests"
```

Expected: every test fails — `OutcomeCategory` is null on every return site except the monotonicity one (already wired in Task 4).

- [ ] **Step 4: Thread `OutcomeCategory` through every return site**

In `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`:

Replace the `Fail` helper to accept and propagate the category:

```csharp
private AutoCalibrationOutcome Fail(string area, string reason, string outcomeCategory)
{
    _logger?.LogInformation("Auto-calibration not attempted for {Area}: {Reason}.", string.IsNullOrEmpty(area) ? "<none>" : area, reason);
    return new AutoCalibrationOutcome(Persisted: false, AreaKey: area, RejectReason: reason, OutcomeCategory: outcomeCategory);
}
```

Update every `Fail(...)` call site in `RunAttemptCoreAsync` to pass the matching `OutcomeVocabulary` constant. Walk the file top-to-bottom; the mapping table is:

| Reason fragment | Outcome category constant |
|---|---|
| `"not in-world …"` | `OutcomeVocabulary.RejectedNoArea` |
| `"Project Gorgon is not the foreground window"` | `OutcomeVocabulary.RejectedPgNotForeground` |
| `"no map bbox set …"` | `OutcomeVocabulary.RejectedNoBbox` |
| `"map capture failed …"` | `OutcomeVocabulary.RejectedCaptureFailed` |
| `"preparing map assets…"` | `OutcomeVocabulary.RejectedNoBaseTexture` |
| `"couldn't locate the map …"` | `OutcomeVocabulary.RejectedMapNotLocated` |
| `"the located map rect fell outside …"` | `OutcomeVocabulary.RejectedClampDegenerate` |

The solver-reject around line 390 doesn't go through `Fail`; update it directly:

```csharp
if (result.Calibration is null)
{
    var reason = result.RejectReason ?? "no geometrically-consistent fit";
    var category = OutcomeVocabulary.RejectSolveSubcategory(result.RejectReason);
    attempt.Outcome = category;
    _logger?.LogInformation("Auto-calibration rejected for {Area}: {Reason}. Prior calibration kept.", area, reason);
    return new AutoCalibrationOutcome(Persisted: false, AreaKey: area, RejectReason: reason, OutcomeCategory: category);
}
```

The monotonicity-reject return (already touched in Task 4) already carries `OutcomeCategory: OutcomeVocabulary.RejectedNotMonotonic`.

The terminal accept return must also carry it:

```csharp
attempt.Outcome = OutcomeVocabulary.Accepted;
_calibrationService.SaveUserRefinement(area, stamped);
_logger?.LogInformation(
    "Auto-calibration persisted for {Area} (residual {Residual:0.00} px, {Inliers} inliers).",
    area, stamped.ResidualPixels, result.InlierCount);
return new AutoCalibrationOutcome(
    Persisted: true,
    AreaKey: area,
    RejectReason: null,
    OutcomeCategory: OutcomeVocabulary.Accepted);
```

- [ ] **Step 5: Run the new tests + the regression sweep**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests
```

Expected: the 8 new tests pass; every pre-existing test still passes. Optional-parameter addition is non-breaking, but double-check any test that destructured the `AutoCalibrationOutcome` positionally.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs \
        tests/Mithril.MapCalibration.Capture.Tests/Fixtures/TestHelpers.cs \
        tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineOutcomeCategoryTests.cs
git commit -m "feat(map-calibration): populate OutcomeCategory on every engine return (#1005)

Every AutoCalibrationOutcome produced by AutoCalibrationEngine now carries
the OutcomeVocabulary constant matching the attempt.Outcome the sink
already writes. Threading: Fail() takes a category; the success path,
solver-reject path, and monotonicity-reject path each set their own.
CalibrationStatusFormatter can now route structurally on this in the
next commit."
```

---

## Task 6: Structural route in `CalibrationStatusFormatter` + new "calibration unchanged" message

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs`
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/CalibrationStatusFormatterTests.cs` (extend the existing test file; create if absent)

- [ ] **Step 1: Write the failing tests**

In `tests/Mithril.MapCalibration.Capture.Tests/CalibrationStatusFormatterTests.cs`, add:

```csharp
[Fact]
public void RejectedNotMonotonic_outcome_gets_its_own_message_not_zoom_out_instruction()
{
    var outcome = new AutoCalibrationOutcome(
        Persisted: false,
        AreaKey: "AreaTest",
        RejectReason: "new inlier count 4 below existing 10 − 2",
        OutcomeCategory: OutcomeVocabulary.RejectedNotMonotonic);

    var msg = CalibrationStatusFormatter.ForOutcome(outcome);

    msg.Should().NotBeNull();
    msg.Should().Contain("Calibration unchanged");
    msg.Should().Contain("clear");
    msg.Should().NotContain("zoom the in-game map all the way out");
}

[Fact]
public void Null_OutcomeCategory_falls_back_to_substring_route_for_legacy_callers()
{
    // A caller that hasn't been updated to populate OutcomeCategory still
    // gets the today's-behaviour chip via ForReject substring matching.
    // Regression guard: don't accidentally break legacy emit sites.
    var outcome = new AutoCalibrationOutcome(
        Persisted: false,
        AreaKey: "AreaTest",
        RejectReason: "residual 25.00 px exceeds threshold 12.00 px",
        OutcomeCategory: null);

    var msg = CalibrationStatusFormatter.ForOutcome(outcome);

    msg.Should().Contain("zoom the in-game map all the way out");
}

[Fact]
public void Persisted_outcome_returns_null_regardless_of_OutcomeCategory()
{
    var outcome = new AutoCalibrationOutcome(
        Persisted: true,
        AreaKey: "AreaTest",
        RejectReason: null,
        OutcomeCategory: OutcomeVocabulary.Accepted);

    CalibrationStatusFormatter.ForOutcome(outcome).Should().BeNull();
}
```

If `CalibrationStatusFormatterTests.cs` doesn't exist yet, create it with the standard header: `using FluentAssertions; using Mithril.MapCalibration.Capture; using Mithril.MapCalibration.Capture.Diagnostics; using Xunit;`.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationStatusFormatterTests"
```

Expected: `RejectedNotMonotonic_outcome_gets_its_own_message…` fails — today's `ForReject` substring-matches "inlier" and routes to "zoom out".

- [ ] **Step 3: Add the structural route**

Replace `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs` with:

```csharp
using System;
using Mithril.MapCalibration.Capture.Diagnostics;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Maps an <see cref="AutoCalibrationOutcome"/> / raw reject reason to the
/// user-facing status-chip string (spec §11). Pure + CI-tested; the push to the
/// overlay chip (<c>IOverlayWindow.SetStatusMessage</c>) is shell wiring.
/// The engine's reject reasons are diagnostic ("residual 25.00 px exceeds
/// threshold…"); this turns them into an actionable instruction.
///
/// <para><b>Routing model (#1005).</b> <see cref="ForOutcome"/> routes
/// structurally on <see cref="AutoCalibrationOutcome.OutcomeCategory"/> first:
/// when set, the outcome category maps deterministically to its user message.
/// When <see langword="null"/> (legacy callers that pre-date #1005),
/// <see cref="ForReject"/> falls back to substring-matching the
/// <see cref="AutoCalibrationOutcome.RejectReason"/> &#8212; preserving the
/// pre-#1005 behaviour for any path that hasn't been updated yet.</para>
/// </summary>
public static class CalibrationStatusFormatter
{
    /// <summary>
    /// The status string for an outcome, or <see langword="null"/> when it
    /// succeeded (a persisted calibration clears the chip — happy state).
    /// </summary>
    public static string? ForOutcome(AutoCalibrationOutcome outcome)
    {
        if (outcome.Persisted) return null;
        return ForCategory(outcome.OutcomeCategory)
               ?? ForReject(outcome.RejectReason ?? "couldn't auto-calibrate the map");
    }

    /// <summary>
    /// Structural route — known <see cref="OutcomeVocabulary"/> categories to
    /// their user-facing messages. Returns <see langword="null"/> for unknown
    /// or null categories so the caller falls back to <see cref="ForReject"/>.
    /// </summary>
    private static string? ForCategory(string? category) => category switch
    {
        OutcomeVocabulary.RejectedNotMonotonic =>
            "Calibration unchanged: the new fit was worse than the saved one. "
            + "To force-replace, clear the saved calibration for this area.",
        // Other categories deliberately not routed here yet — they fall through
        // to the substring path so today's wording is preserved by default.
        _ => null,
    };

    /// <summary>Map a raw reject reason to an actionable user instruction.</summary>
    public static string ForReject(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "Couldn't auto-calibrate the map.";

        // No bbox framed yet → tell them to draw it.
        if (Contains(reason, "bbox"))
            return "No map region set — use the draw-map-bbox hotkey to frame the map.";

        // PG not focused / not in-world → name the game.
        if (Contains(reason, "foreground") || Contains(reason, "in-world") || Contains(reason, "not detected"))
            return "Open Project Gorgon (focused, in an area) to calibrate the map.";

        // Assets still extracting.
        if (Contains(reason, "map assets") || Contains(reason, "preparing") || Contains(reason, "base texture"))
            return "Preparing map assets… try the capture again in a moment.";

        // Low-confidence solve (residual / inliers) → the actionable fix is to
        // zoom the in-game map all the way out and redraw the bbox.
        if (Contains(reason, "residual") || Contains(reason, "inlier")
            || Contains(reason, "fit") || Contains(reason, "locate the map") || Contains(reason, "capture"))
            return "Couldn't auto-calibrate — zoom the in-game map all the way out, then redraw the map bbox and retry.";

        return "Couldn't auto-calibrate the map.";
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationStatusFormatterTests"
```

Expected: all green (the 3 new tests + any pre-existing tests in the file).

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs \
        tests/Mithril.MapCalibration.Capture.Tests/CalibrationStatusFormatterTests.cs
git commit -m "fix(map-calibration): route RejectedNotMonotonic chip away from the zoom-out loop (#1005)

ForOutcome now routes structurally on OutcomeCategory first, falling back
to the existing ForReject substring path when the category is null. The
new RejectedNotMonotonic message tells the user the calibration was kept
intentionally and points at the clear-current-area path, instead of
sending them to the action that just tripped the gate."
```

---

## Task 7: End-to-end repro of the originating bug

**Files:**
- Test: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineZoomChangeRegressionTests.cs` (create)

Single end-to-end test that walks the exact #1005 user scenario through the engine + formatter together. If this fails, the bug is back.

- [ ] **Step 1: Write the failing scenario test**

Create `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineZoomChangeRegressionTests.cs`:

```csharp
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #1005 regression: a user calibrates at one in-game zoom, then re-captures
/// at a different zoom. The pre-#1005 monotonicity gate rejected the second
/// capture because the per-attempt inlier count drops with visible-icon size
/// (RenderSizePx-16 typed detection), and the chip then told the user to
/// "zoom the in-game map all the way out" — the action that just tripped
/// the gate. Both sides of the loop are tested end-to-end here.
/// </summary>
public sealed class AutoCalibrationEngineZoomChangeRegressionTests
{
    private const string Area = "AreaEltibule";

    [Fact]
    public async Task User_recalibrates_at_a_different_zoom_and_lands_without_being_told_to_zoom_out()
    {
        var svc = new FakeCalibrationService();

        // Step 1: cold-start calibration at scale 0.408. High quality fit.
        var coldHarness = new EngineHarness
        {
            Service = svc,
            Solve = TestHelpers.Accepted(residual: 0.79, inliers: 10),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.408)),
        };
        var first = await coldHarness.Engine().TryCalibrateCurrentAreaAsync(default);

        first.Persisted.Should().BeTrue();
        first.OutcomeCategory.Should().Be(OutcomeVocabulary.Accepted);
        CalibrationStatusFormatter.ForOutcome(first).Should().BeNull();
        svc.Saved[Area].LocatorScale.Should().Be(0.408);

        // Step 2: user changes the in-game zoom, redraws the bbox, re-captures.
        // The new regime is scale 0.800 — outside the ±2% tolerance. Even though
        // the fit at the new zoom has fewer inliers than the old (icons render
        // smaller, fewer survive RenderSizePx-16 matching), the gate must skip
        // because the comparison is invalid across regimes.
        var zoomChangedHarness = new EngineHarness
        {
            Service = svc,
            Solve = TestHelpers.Accepted(residual: 1.2, inliers: 5),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.800)),
        };
        var second = await zoomChangedHarness.Engine().TryCalibrateCurrentAreaAsync(default);

        second.Persisted.Should().BeTrue();
        second.OutcomeCategory.Should().Be(OutcomeVocabulary.Accepted);
        CalibrationStatusFormatter.ForOutcome(second).Should().BeNull(); // chip clears

        // Saved cal is the NEW one (single-slot storage per #1005; per-scale is #1006).
        svc.Saved[Area].LocatorScale.Should().Be(0.800);
        svc.Saved[Area].ResidualPixels.Should().Be(1.2);
    }

    [Fact]
    public async Task Wrong_fit_at_the_same_zoom_still_keeps_the_good_one_with_the_new_chip_message()
    {
        // The original #988 protection. Same in-game zoom, second capture is
        // a 5× residual blow-up with fewer inliers — clearly wrong. Gate
        // fires, chip says "unchanged".
        var svc = new FakeCalibrationService();

        var firstHarness = new EngineHarness
        {
            Service = svc,
            Solve = TestHelpers.Accepted(residual: 0.79, inliers: 10),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.408)),
        };
        await firstHarness.Engine().TryCalibrateCurrentAreaAsync(default);

        var secondHarness = new EngineHarness
        {
            Service = svc,
            Solve = TestHelpers.Accepted(residual: 4.03, inliers: 4),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.411)), // within ±2%
        };
        var second = await secondHarness.Engine().TryCalibrateCurrentAreaAsync(default);

        second.Persisted.Should().BeFalse();
        second.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNotMonotonic);
        var msg = CalibrationStatusFormatter.ForOutcome(second);
        msg.Should().Contain("Calibration unchanged");
        msg.Should().NotContain("zoom the in-game map all the way out");

        // Good first calibration preserved.
        svc.Saved[Area].ResidualPixels.Should().Be(0.79);
    }
}
```

- [ ] **Step 2: Run to verify it passes**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngineZoomChangeRegressionTests"
```

Expected: PASS (2 tests). If either fails, return to the relevant earlier task.

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineZoomChangeRegressionTests.cs
git commit -m "test(map-calibration): end-to-end zoom-change regression for #1005

Two-step scenario: cold-start cal at scale 0.408, then re-capture at scale
0.800 lands without a chip nag; cold-start cal at scale 0.408, then wrong
re-fit at scale 0.411 is rejected with the new \"calibration unchanged\"
chip (not the zoom-out loop message)."
```

---

## Task 8: Full-solution verification + PR + planning index flip

**Files:** none directly; updates `docs/planning/INDEX.md` row status on merge.

- [ ] **Step 1: Run the full solution test suite**

```bash
dotnet test Mithril.slnx
```

Expected: all green. `AutoCalibrationOutcome` is a public type — if a non-MapCalibration test fails because of the new positional field, investigate (the default makes it non-breaking, but a downstream test may have destructured positionally).

- [ ] **Step 2: Build at warnings-as-errors**

```bash
dotnet build Mithril.slnx --warningsAsErrors
```

Expected: green.

- [ ] **Step 3: Open the PR**

```bash
gh pr create \
  --base main \
  --title "fix(map-calibration): scale-aware monotonicity gate (#1005)" \
  --body "$(cat <<'EOF'
## Summary

Closes #1005.

- Adds `AreaCalibration.LocatorScale` (additive nullable; no schema bump).
- `AutoCalibrationEngine` stamps the candidate's `LocatorScale` from `LocateMetrics.Scale` and skips the #988 monotonicity check when the existing and candidate regimes differ by more than 2% (or either side is null/degenerate).
- `AutoCalibrationOutcome` gains an `OutcomeCategory` field; every engine return site populates it; `CalibrationStatusFormatter.ForOutcome` routes structurally on it.
- `RejectedNotMonotonic` gets its own chip message ("Calibration unchanged…") instead of the misleading "zoom the in-game map all the way out" loop.
- Storage stays single-slot; per-scale storage is the follow-up at #1006.

Spec + plan: [`docs/planning/calibration-1005-scale-aware-gate/`](docs/planning/calibration-1005-scale-aware-gate/).

## Test plan

- [ ] Eltibule 03:11:05 / 03:11:30 pair stays protected (covered by `Same_scale_regime_still_protects_a_good_fit_from_a_worse_one` and `Wrong_fit_at_the_same_zoom_still_keeps_the_good_one_with_the_new_chip_message`).
- [ ] Zoom-change scenario lands without the chip nag (`User_recalibrates_at_a_different_zoom_and_lands_without_being_told_to_zoom_out`).
- [ ] Legacy `null` `LocatorScale` on existing record → gate skipped (`Legacy_null_LocatorScale_on_existing_skips_the_gate`).
- [ ] Manual: launch Mithril, capture at one in-game zoom, capture again at a different zoom, confirm overlay updates and no looping chip.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 4: After the PR merges, flip the INDEX.md row**

Edit `docs/planning/INDEX.md` and change the `calibration-1005-scale-aware-gate` row's status from `active` to `shipped`. Commit on main (or in a follow-up PR):

```bash
git commit -am "docs(planning): flip calibration-1005-scale-aware-gate to shipped"
```

---

## Self-review

**Spec coverage** — Walked the `## Definition of done` list in `spec.md` against the tasks:

- ✅ `AreaCalibration.LocatorScale` added as additive nullable → Task 1.
- ✅ `AutoCalibrationEngine` stamps and gate-skips → Task 4.
- ✅ `AutoCalibrationOutcome.OutcomeCategory` populated at every return site → Tasks 2 + 5.
- ✅ `CalibrationStatusFormatter.ForOutcome` routes structurally with fallback → Task 6.
- ✅ Same-regime worse fit rejected → Task 4 + Task 7.
- ✅ Same-regime better fit accepted → covered by existing engine tests; re-asserted in Task 7's first scenario step.
- ✅ Different-regime accepted regardless → Task 4 + Task 7.
- ✅ Either-side null skips the gate → Tasks 3 + 4.
- ✅ Tolerance at-boundary + degenerate-value tests → Task 3.
- ✅ `OutcomeCategory = RejectedNotMonotonic` → new message → Task 6.
- ✅ `OutcomeCategory = null` + monotonicity-style reason → fallback → Task 6.
- ✅ End-to-end zoom-change regression → Task 7.

**Type-name consistency** — `LocatorScale`, `IsSameScaleRegime`, `OutcomeCategory`, `RejectedNotMonotonic` used identically across all tasks. `LocateMetrics.Scale` is the source; `AreaCalibration.LocatorScale` is the persisted copy.

**Placeholder scan** — no "TBD", "implement later", or vague-error-handling boilerplate. Every code step contains complete code. The only conditional step is Task 5 Step 1 ("extract helpers if not already shared") — gated on a check the implementer makes against the live file.
