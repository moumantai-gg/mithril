# Shadow-mode synthesis-J observability — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface the Shadow-mode synthesis-J verdict in two artifacts a #1116-style investigator already has on disk — the per-attempt diagnostic bundle's `01-attempt.json` (additive schema v2 → v3) and the Mithril per-day Serilog file — so threshold-tuning conversations can be grounded in measurement instead of speculation.

**Architecture:** Pure additive observability layer over the existing `MapCalibrationSolveEngine`. The engine populates a new `SynthesisDiagnostics` record on `CalibrationSolveResult`; the bundle sink and a new Shadow-mode `LogInformation` line both read from there. The existing Meter / ActivitySource emit in `EmitSynthesisRerankTelemetry` stays as the OTLP / perf-trace source of truth (unchanged). A small `ComputeVerdicts` helper is extracted from the existing inline verdict logic so the bundle + new log + existing meter emit all share one definition.

**Tech Stack:** C# 12 / .NET 10 (`net10.0-windows`), `System.Text.Json` source-gen (existing `CalibrationBundleJsonContext`), `Microsoft.Extensions.Logging`, xunit + FluentAssertions for tests.

**Spec:** [`spec.md`](spec.md). **Issue:** [mithril#1117](https://github.com/moumantai-gg/mithril/issues/1117). **Blocks:** path 1 of [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116).

---

## Repo bearings (read before Task 0)

- Working tree: a git worktree of `moumantai-gg/mithril` checked out to `main`. From the worktree root, the relevant files are:
  - **Engine** — [`src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs) (the `Solve` flow + `EmitSynthesisRerankTelemetry` + `SynthesisOrientationWinner` record at lines ~507-520 + `CalibrationSolveResult` record at lines ~499-506).
  - **Engine options** — [`src/Mithril.MapCalibration/MapCalibrationSolverOptions.cs`](../../../src/Mithril.MapCalibration/MapCalibrationSolverOptions.cs) (the `SynthesisRerankMode` enum + `SynthesisJMin` / `SynthesisNMin` defaults).
  - **Bundle JSON schema** — [`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs) (records + source-gen context).
  - **Bundle sink** — [`src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs) (where `01-attempt.json` is built).
  - **Engine logging tests** — [`tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineLoggingTests.cs`](../../../tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineLoggingTests.cs) (has `CapturingLogger` / `FixedDetector` / `AlwaysRejectGate` fixtures we'll reuse).
  - **Bundle sink tests** — [`tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs`](../../../tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs) (has `PopulatedAccepted` fixture).
- Solution file: `Mithril.slnx` at repo root.

## Build / test commands

- **Build entire solution:** `dotnet build Mithril.slnx`
- **Run all tests:** `dotnet test Mithril.slnx`
- **Run the two affected test projects only (fast iteration):**
  - `dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj`
  - `dotnet test tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj`
- **Run a single test by name:** `dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~Shadow_mode_emits_synthesis_summary_line"`

## Gotchas to watch (carried over from project memory)

- **Close Mithril.exe before any `dotnet build` / `dotnet test`.** The shell's single-instance mutex holds module DLLs open and produces silent partial builds (memory: `mithril_build_file_lock_silent`, `mithril_single_instance_mutex_masks_worktree_build`). A `PreToolUse` hook at `.claude/check-mithril-running.ps1` *should* block these commands while the shell runs, but it has been observed to miss `.claude/worktrees/` paths (`mithril_running_hook_misses_claude_worktrees`) — verify Mithril is closed manually.
- **Don't reflexively wipe `bin/obj` if tests flake.** `Directory.Build.targets` already runs `CleanBinObj`; re-adding a delete elsewhere is a documented anti-pattern (memory: `stale_binobj_dont_repatch_startps1`).
- **Commit with `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`** per the standard project trailer.
- **Do NOT push to main; branch + PR.** Branch posture for this work: a single branch off `main`, one PR.

---

## Task 0: Branch + baseline build

**Goal:** Confirm we're on the right starting point and the test suite is green BEFORE touching anything.

**Files:** none modified.

- [ ] **Step 1: Verify the worktree state.**

```bash
git status --short
git branch --show-current
git log --oneline -3
```

Expected:
- Working tree has the spec + plan + INDEX edits uncommitted (`M docs/planning/INDEX.md`, untracked `docs/planning/calibration-1117-synthesis-j-observability/`). These were authored during the brainstorming step and are committed in Step 5.
- Currently on the brainstorming-step worktree branch (`claude/cranky-jones-…` or similar). HEAD's last few commits should be from the upstream `main` (no in-flight #1117 implementation work).

- [ ] **Step 2: Decide on the working branch.**

If the worktree's branch is already `claude/1117-synthesis-j-observability`, skip this step.

If the worktree was named after the brainstorming run (`claude/cranky-jones-…`) and you want a more descriptive PR branch name, rename it in place:

```bash
git branch -m claude/1117-synthesis-j-observability
```

This preserves the working-tree contents and just renames the local branch. The eventual `git push -u origin claude/1117-synthesis-j-observability` in Task 8 will publish it as a fresh remote branch.

(Renaming is optional — leaving the auto-generated branch name works too; just adjust the `git push` in Task 8 to match.)

- [ ] **Step 3: Confirm Mithril.exe is NOT running.**

```powershell
Get-Process Mithril -ErrorAction SilentlyContinue
```

Expected: no output. If output appears, close the shell before proceeding.

- [ ] **Step 4: Baseline build + test.**

```bash
dotnet build Mithril.slnx
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj
dotnet test tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj
```

Expected: build succeeds, both test projects pass. Note the test counts so we can confirm later tasks don't regress anything.

- [ ] **Step 5: Commit the spec + plan + INDEX row.**

```bash
git add docs/planning/calibration-1117-synthesis-j-observability/spec.md docs/planning/calibration-1117-synthesis-j-observability/plan.md docs/planning/INDEX.md
git commit -m "$(cat <<'EOF'
docs(planning): spec + plan for #1117 shadow-mode synthesis-J observability

Adds docs/planning/calibration-1117-synthesis-j-observability/{spec,plan}.md and
the INDEX row. Implementation lands in subsequent commits on this branch.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 1: `SynthesisDiagnostics` record + `CalibrationSolveResult.Synthesis` field

**Goal:** Add the pure-data record + the new init property. No engine behavior change yet — just types so subsequent tasks have something to populate.

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` (add the record alongside the existing `SynthesisOrientationWinner` at lines ~507-520; add the init property on `CalibrationSolveResult` at lines ~499-506).
- Test: `tests/Mithril.MapCalibration.Tests/Detection/SynthesisDiagnosticsTests.cs` (new file).

- [ ] **Step 1: Write the failing test.**

Create `tests/Mithril.MapCalibration.Tests/Detection/SynthesisDiagnosticsTests.cs`:

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class SynthesisDiagnosticsTests
{
    [Fact]
    public void SynthesisDiagnostics_record_carries_all_required_fields()
    {
        var d = new SynthesisDiagnostics(
            Mode: "shadow",
            Rotate180: false,
            J: 7.5,
            JMin: 8.0,
            RefsAboveHalf: 6,
            RefsTotal: 11,
            RefsOffCrop: 2,
            NMin: 8,
            Verdict: "reject",
            GateVerdict: "accept",
            Disagree: true,
            DisagreeChange: "accept_to_reject");

        d.Mode.Should().Be("shadow");
        d.Rotate180.Should().Be(false);
        d.J.Should().Be(7.5);
        d.RefsAboveHalf.Should().Be(6);
        d.RefsTotal.Should().Be(11);
        d.RefsOffCrop.Should().Be(2);
        d.Verdict.Should().Be("reject");
        d.GateVerdict.Should().Be("accept");
        d.Disagree.Should().BeTrue();
        d.DisagreeChange.Should().Be("accept_to_reject");
    }

    [Fact]
    public void CalibrationSolveResult_Synthesis_defaults_to_null()
    {
        var result = new CalibrationSolveResult(
            Calibration: null, InlierCount: 0, RejectReason: "no detections");

        result.Synthesis.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~SynthesisDiagnosticsTests"
```

Expected: compilation error — `SynthesisDiagnostics` is undefined, and `CalibrationSolveResult` has no `Synthesis` property.

- [ ] **Step 3: Add `SynthesisDiagnostics` record.**

In `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs`, after the existing `internal sealed record SynthesisOrientationWinner(...)` declaration near the bottom of the file (lines ~508-520), add:

```csharp
/// <summary>
/// Per-attempt diagnostic snapshot of the synthesis-J re-rank result. Populated
/// on <see cref="CalibrationSolveResult.Synthesis"/> whenever synthesis ran
/// (mode != Off), regardless of which gate drove the outcome. Surfaced to both
/// the diagnostic bundle (01-attempt.json synthesis section, #1117) and the
/// Shadow-mode Serilog mirror — one engine population, two consumers.
/// </summary>
public sealed record SynthesisDiagnostics(
    string Mode,              // "shadow" | "enabled"  (never "off" — record is null in that case)
    bool? Rotate180,          // null when no orientation produced a winner
    double? J,                // null when no winner
    double JMin,
    int? RefsAboveHalf,       // null when no winner
    int? RefsTotal,           // null when no winner
    int? RefsOffCrop,         // null when no winner
    int NMin,
    string Verdict,           // "accept" | "reject" | "no_winner"
    string GateVerdict,       // legacy gate verdict, "accept" | "reject"
    bool Disagree,            // synthesis verdict differs from legacy gate verdict
    string? DisagreeChange);  // "reject_to_accept" | "accept_to_reject" | null
```

- [ ] **Step 4: Add `Synthesis` init property to `CalibrationSolveResult`.**

In the same file, modify the existing `CalibrationSolveResult` record (lines ~499-506) to add the new init-only property:

```csharp
public sealed record CalibrationSolveResult(
    AreaCalibration? Calibration,
    int InlierCount,
    string? RejectReason,
    IReadOnlyList<TypeAwareRansacSolver.AssignedReference>? Inliers = null)
{
    public IReadOnlyList<TypedDetection>? Detections { get; init; }
    public SynthesisDiagnostics? Synthesis { get; init; }   // #1117
}
```

- [ ] **Step 5: Run the tests to verify they pass.**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~SynthesisDiagnosticsTests"
```

Expected: 2 tests pass.

- [ ] **Step 6: Run the full Detection test project to confirm no regressions.**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj
```

Expected: same total count as Task 0 baseline + 2 new passes.

- [ ] **Step 7: Commit.**

```bash
git add src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs tests/Mithril.MapCalibration.Tests/Detection/SynthesisDiagnosticsTests.cs
git commit -m "$(cat <<'EOF'
feat(map-calibration): add SynthesisDiagnostics record + CalibrationSolveResult.Synthesis field (#1117)

Per-attempt synthesis-J snapshot, pure-data record. Init-only property on
CalibrationSolveResult so two consumers (diagnostic bundle + Shadow-mode
Serilog mirror) can read one engine-populated source.

No engine behavior change — types only. Population lands in Task 3.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Extract `ComputeVerdicts` helper

**Goal:** Pull the synth/gate/disagree/change computation out of `EmitSynthesisRerankTelemetry` (currently inline lines ~190-235) into a private static helper so the bundle path, the new log line, and the existing meter emit all share one definition. Pure refactor — existing tests must stay green.

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs`.

- [ ] **Step 1: Re-confirm baseline by running the engine logging tests.**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~MapCalibrationSolveEngine"
```

Expected: all pass. Note the count — it must not drop after this refactor.

- [ ] **Step 2: Add the helper method.**

In `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs`, add this private method on the `MapCalibrationSolveEngine` class (location: after `EmitSynthesisRerankTelemetry`, before `HasAnyMeterListener`):

```csharp
/// <summary>
/// Resolve synth/gate/disagree/change for a single solve attempt. Shared by the
/// span/meter emit (<see cref="EmitSynthesisRerankTelemetry"/>), the bundle
/// SynthesisDiagnostics population, and the Shadow-mode Serilog mirror (#1117).
///
/// <para>Returns <c>DisagreeChange == null</c> when the two gates agree (the existing
/// span tag <c>disagree.would_change</c> renders this as the literal string
/// <c>"none"</c> — the conversion happens at the span call site, not here, so the
/// helper's contract is the semantic truth).</para>
/// </summary>
private (string SynthVerdict, string GateVerdict, bool Disagree, string? DisagreeChange)
    ComputeVerdicts(
        SynthesisOrientationWinner? winner,
        CalibrationSolveResult finalResult,
        SynthesisRerankMode mode)
{
    bool legacyAccept;
    if (mode == SynthesisRerankMode.Shadow)
    {
        // Shadow: legacy gate is source of truth; finalResult.Calibration reflects its verdict.
        legacyAccept = finalResult.Calibration is not null;
    }
    else
    {
        // Enabled: re-run the legacy gate against the synthesis winner so the disagreement
        // counter remains meaningful even though synthesis-J is doing the final accept.
        legacyAccept = winner is not null
            && _gate.Accept(winner.Calibration, winner.Inliers.Count, out _);
    }

    bool synthAccept = mode == SynthesisRerankMode.Enabled
        ? finalResult.Calibration is not null
        : winner is not null
          && winner.J >= _options.SynthesisJMin
          && winner.RefsAboveHalf >= _options.SynthesisNMin;

    var synthVerdict = synthAccept ? "accept" : "reject";
    var gateVerdict = legacyAccept ? "accept" : "reject";
    var disagree = synthAccept != legacyAccept;
    var change = disagree
        ? (synthAccept ? "reject_to_accept" : "accept_to_reject")
        : (string?)null;
    return (synthVerdict, gateVerdict, disagree, change);
}
```

- [ ] **Step 3: Replace the inline computation in `EmitSynthesisRerankTelemetry` with a call to the helper.**

The existing code at lines ~190-235 of `MapCalibrationSolveEngine.cs` computes `legacyAccept` AND `legacyInlierCount` AND `legacyResidualPx` together in one branched block, then computes `synthesisAccept` / `synthVerdict` / `gateVerdict` / `disagree` / `change`. The refactor extracts the verdict computation only; `legacyInlierCount` and `legacyResidualPx` stay inline because they feed the meter records (lines 251-252 / 257-262), not the verdict.

Replace the entire block from `bool legacyAccept;` (line ~190) through `: "none";` (line ~235) with:

```csharp
// Verdicts (incl. disagree-change) shared with the bundle-population path
// and the Shadow-mode Serilog mirror (#1117).
var (synthVerdict, gateVerdict, disagree, changeOrNull) = ComputeVerdicts(winner, finalResult, mode);
var change = changeOrNull ?? "none";  // preserve existing span tag literal

// Residual + inlier count stay inline because they're not verdict-related —
// they feed the meter records below, not the helper.
int legacyInlierCount;
double? legacyResidualPx;
if (mode == SynthesisRerankMode.Shadow)
{
    legacyInlierCount = finalResult.InlierCount;
    legacyResidualPx = finalResult.Calibration?.ResidualPixels;
}
else if (winner is not null)
{
    // Enabled with a winner: report the winner's residual + inlier count.
    legacyInlierCount = winner.Inliers.Count;
    legacyResidualPx = winner.Calibration.ResidualPixels;
}
else
{
    // Enabled with no winner: nothing to report.
    legacyInlierCount = 0;
    legacyResidualPx = null;
}
```

(The remainder of `EmitSynthesisRerankTelemetry` — the `if (span is not null)` block + the meter `Record` / `Add` calls — is untouched.)

- [ ] **Step 4: Build + run the full Detection test project to confirm no regressions.**

```bash
dotnet build Mithril.slnx
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj
```

Expected: build succeeds; same test count + same pass count as Task 0 baseline + the 2 added in Task 1.

- [ ] **Step 5: Commit.**

```bash
git add src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs
git commit -m "$(cat <<'EOF'
refactor(map-calibration): extract ComputeVerdicts from EmitSynthesisRerankTelemetry (#1117)

Pull synth/gate/disagree/change derivation into a private helper so the
upcoming bundle-population path + Shadow-mode Serilog mirror share one
definition with the existing meter/span emit. Pure refactor — span tag
"disagree.would_change" still emits literal "none" at the span call site
when the two gates agree.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Populate `result.Synthesis` in `Solve`

**Goal:** Engine populates the new `Synthesis` field on every `CalibrationSolveResult` whenever synthesis ran (mode != Off). Bundle + log surfaces consume from there in subsequent tasks.

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` (the `Solve` method).
- Test: `tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineSynthesisWiringTests.cs` (new).

- [ ] **Step 1: Write the failing tests.**

Create `tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineSynthesisWiringTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// #1117: CalibrationSolveResult.Synthesis is populated whenever synthesis-J ran,
/// regardless of which mode drove the gate verdict. Null only when mode == Off.
/// </summary>
public sealed class MapCalibrationSolveEngineSynthesisWiringTests
{
    [Fact]
    public void Off_mode_leaves_Synthesis_null()
    {
        var (detector, refs, request) = BuildFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Off };
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger: null, options: options);

        var result = engine.Solve(request, refs);

        result.Synthesis.Should().BeNull();
    }

    [Fact]
    public void Shadow_mode_populates_Synthesis_with_mode_shadow()
    {
        var (detector, refs, request) = BuildFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Shadow };
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger: null, options: options);

        var result = engine.Solve(request, refs);

        result.Synthesis.Should().NotBeNull();
        result.Synthesis!.Mode.Should().Be("shadow");
        result.Synthesis.JMin.Should().Be(options.SynthesisJMin);
        result.Synthesis.NMin.Should().Be(options.SynthesisNMin);
    }

    [Fact]
    public void Enabled_mode_populates_Synthesis_with_mode_enabled()
    {
        var (detector, refs, request) = BuildFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Enabled };
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger: null, options: options);

        var result = engine.Solve(request, refs);

        result.Synthesis.Should().NotBeNull();
        result.Synthesis!.Mode.Should().Be("enabled");
    }

    private static (ICalibrationDetector Detector, List<LandmarkReference> Refs, DetectionRequest Request) BuildFixture()
    {
        // One Portal detection, one Portal reference — both type vocabularies overlap so RANSAC
        // runs and synthesis scores ARE computed (degenerate fixture is fine — we only care
        // that the synthesis pathway executed and populated the diagnostics field).
        var detections = new Dictionary<string, IReadOnlyList<TypedDetection>>(StringComparer.Ordinal)
        {
            ["Portal"] = new[] { new TypedDetection("Portal", "icon", new CroppedFramePixel(2, 2), 0.9) },
        };
        var detector = new FixedDetector(detections);
        var refs = new List<LandmarkReference>
        {
            new("Portal", "Test Portal", new WorldCoord(1, 0, 2)),
        };
        var img = new GrayImage(8, 8, new byte[64]);
        var rect = new MapRect(0, 0, 8, 8, 8, 8);
        var request = new DetectionRequest(img, img, rect, IconTemplateSet.Empty, RimMaskMode.None,
            LowNcc: 0.5, TypeFloor: 0.45,
            BlobOptions: new BlobOptions(MinArea: 8, MaxIconArea: 1500, MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5));
        return (detector, refs, request);
    }

    private sealed class FixedDetector : ICalibrationDetector
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> _result;
        public FixedDetector(IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> result) => _result = result;
        public IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> Detect(DetectionRequest request) => _result;
    }

    private sealed class AlwaysRejectGate : ICalibrationConfidenceGate
    {
        public bool Accept(AreaCalibration solve, int inlierCount, out string? rejectReason)
        {
            rejectReason = "test-reject";
            return false;
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~MapCalibrationSolveEngineSynthesisWiringTests"
```

Expected: tests fail because `Solve` does not yet populate `Synthesis`.

- [ ] **Step 3: Populate `result.Synthesis` in `Solve`.**

In `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs`, find the end of the `Solve` method where `legacyResult` / `finalResult` is about to be returned (both the `mode != Enabled` branch and the `Enabled` branch). Add a helper for building the `SynthesisDiagnostics`:

```csharp
private SynthesisDiagnostics? BuildSynthesisDiagnostics(
    SynthesisOrientationWinner? winner,
    CalibrationSolveResult finalResult,
    SynthesisRerankMode mode)
{
    if (mode == SynthesisRerankMode.Off) return null;

    var (synthVerdict, gateVerdict, disagree, change) = ComputeVerdicts(winner, finalResult, mode);
    return new SynthesisDiagnostics(
        Mode: mode == SynthesisRerankMode.Enabled ? "enabled" : "shadow",
        Rotate180: winner?.Rotate180,
        J: winner?.J,
        JMin: _options.SynthesisJMin,
        RefsAboveHalf: winner?.RefsAboveHalf,
        RefsTotal: winner?.RefsTotal,
        RefsOffCrop: winner?.RefsOffCrop,
        NMin: _options.SynthesisNMin,
        Verdict: winner is null ? "no_winner" : synthVerdict,
        GateVerdict: gateVerdict,
        Disagree: disagree,
        DisagreeChange: change);
}
```

Then, in both return paths of `Solve` (the `mode != Enabled` legacy-return branch and the `Enabled` branch), attach the diagnostics to the result before returning. The cleanest way is to use `record with`:

In the `mode != Enabled` branch (currently around lines 109-127), change the `return legacyResult;` line to:

```csharp
return legacyResult with { Synthesis = BuildSynthesisDiagnostics(bestSynthesis, legacyResult, mode) };
```

In the `Enabled` branch (currently around lines 130-160) there are THREE return points, not two:

1. **No-winner path** (currently lines 130-137): synthesis ran but produced no winner. Change the `var noWinner = new CalibrationSolveResult(...) { Detections = ... };` line so the result carries Synthesis:

```csharp
var noWinner = new CalibrationSolveResult(null, 0, "no synthesis-J winner",
    legacyResult.Inliers) { Detections = legacyResult.Detections };
noWinner = noWinner with { Synthesis = BuildSynthesisDiagnostics(bestSynthesis, noWinner, mode) };
EmitSynthesisRerankTelemetry(mode, bestSynthesis, noWinner);
return noWinner;
```

(`bestSynthesis` is null here; `BuildSynthesisDiagnostics` returns a non-null `SynthesisDiagnostics` with `Verdict = "no_winner"` per the `winner is null ? "no_winner" : synthVerdict` ternary in the helper.)

2. **Synthesis-accept path** (currently lines 141-149): append `with`:

```csharp
finalResult = new CalibrationSolveResult(
    bestSynthesis.Calibration, bestSynthesis.Inliers.Count, null, bestSynthesis.Inliers)
    { Detections = legacyResult.Detections };
finalResult = finalResult with { Synthesis = BuildSynthesisDiagnostics(bestSynthesis, finalResult, mode) };
```

3. **Synthesis-reject path** (currently lines 150-156): same `with` append after the `finalResult = new ...` line:

```csharp
finalResult = new CalibrationSolveResult(null, bestSynthesis.Inliers.Count, reason, bestSynthesis.Inliers)
    { Detections = legacyResult.Detections };
finalResult = finalResult with { Synthesis = BuildSynthesisDiagnostics(bestSynthesis, finalResult, mode) };
```

(All three paths use the same `with`-append pattern so the assignment order — build the result, then attach diagnostics that reference it — stays consistent.)

- [ ] **Step 4: Run the tests to verify they pass.**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~MapCalibrationSolveEngineSynthesisWiringTests"
```

Expected: 3 passes.

- [ ] **Step 5: Run the full Detection test project to confirm no regressions.**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj
```

Expected: same baseline pass count + 2 (Task 1) + 3 (this task).

- [ ] **Step 6: Commit.**

```bash
git add src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineSynthesisWiringTests.cs
git commit -m "$(cat <<'EOF'
feat(map-calibration): populate CalibrationSolveResult.Synthesis in Solve (#1117)

Adds BuildSynthesisDiagnostics helper; Solve attaches the diagnostics to every
result whenever mode != Off. Both Shadow + Enabled populate; Off leaves null.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `SynthesisJson` bundle wire-format + schema bump

**Goal:** Bundle schema gets the new optional `synthesis` field. Schema-only — sink wiring lands in Task 5.

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`.
- Test: `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/SynthesisJsonRoundTripTests.cs` (new).

- [ ] **Step 1: Write the failing round-trip test.**

Create `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/SynthesisJsonRoundTripTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class SynthesisJsonRoundTripTests
{
    [Fact]
    public void SynthesisJson_round_trips_through_source_gen_context()
    {
        var original = new SynthesisJson(
            SchemaVersion: 1,
            Mode: "shadow",
            Rotate180: false,
            J: 7.5,
            JMin: 8.0,
            RefsAboveHalf: 6,
            RefsTotal: 11,
            RefsOffCrop: 2,
            NMin: 8,
            Verdict: "reject",
            GateVerdict: "accept",
            Disagree: true,
            DisagreeChange: "accept_to_reject");

        var json = JsonSerializer.Serialize(original, CalibrationBundleJsonContext.Default.SynthesisJson);
        var parsed = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.SynthesisJson);

        parsed.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void AttemptJson_schemaVersion_is_3_and_carries_optional_Synthesis()
    {
        var attempt = new AttemptJson(
            SchemaVersion: 3,
            Area: "Map_Test",
            AttemptStartedUtc: "2026-06-08T19:37:13Z",
            AttemptFinalizedUtc: "2026-06-08T19:37:14Z",
            Outcome: "accepted",
            RejectReason: null,
            EngineVersion: "1.0.0",
            Files: new AttemptFilesJson(null, null, null, null, null, null, null, null, null, null),
            LocatorBest: null,
            Synthesis: new SynthesisJson(
                SchemaVersion: 1, Mode: "shadow", Rotate180: false,
                J: 2.0, JMin: 8.0, RefsAboveHalf: 1, RefsTotal: 4, RefsOffCrop: 0,
                NMin: 8, Verdict: "reject", GateVerdict: "accept",
                Disagree: true, DisagreeChange: "accept_to_reject"));

        var json = JsonSerializer.Serialize(attempt, CalibrationBundleJsonContext.Default.AttemptJson);

        json.Should().Contain("\"schemaVersion\": 3");
        json.Should().Contain("\"synthesis\":");
        json.Should().Contain("\"disagreeChange\": \"accept_to_reject\"");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj --filter "FullyQualifiedName~SynthesisJsonRoundTripTests"
```

Expected: compilation error — `SynthesisJson` is undefined and `AttemptJson` has no `Synthesis` parameter.

- [ ] **Step 3: Add the `SynthesisJson` record + bump `AttemptJson`.**

Modify `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`:

Replace the existing `AttemptJson` record (lines ~6-20) with the v3 form:

```csharp
public sealed record AttemptJson(
    int SchemaVersion,
    string Area,
    string AttemptStartedUtc,
    string AttemptFinalizedUtc,
    string Outcome,
    string? RejectReason,
    string EngineVersion,
    AttemptFilesJson Files,
    // Coarse locator's raw fit rect + FM-style inlier/transform metrics + the gate
    // verdict that produced the engine's outcome. Populated on both accept and
    // rejected-map-not-located so the bundle is self-triaging for future
    // close-miss-vs-catastrophic-mismatch rejections. Null when the locator never
    // ran (early pre-locate rejects) or the captured frame had no viable fit.
    LocatorBestJson? LocatorBest = null,
    // Per-attempt synthesis-J snapshot (#1117). Null when SynthesisRerankMode == Off
    // or when this bundle was written by a pre-#1117 engine version (schema v1/v2).
    SynthesisJson? Synthesis = null);
```

Add the `SynthesisJson` record below the existing `RecoveredCalibrationJson` (right before the `JsonSerializable` attributes):

```csharp
/// <summary>
/// Bundle wire-format mirror of <see cref="Mithril.MapCalibration.Detection.SynthesisDiagnostics"/>.
/// SchemaVersion 1 — first persisted version. Null on <see cref="AttemptJson.Synthesis"/>
/// when synthesis did not run (<c>SynthesisRerankMode == Off</c>) or when the bundle was
/// written by a pre-#1117 engine version (schema v1/v2 AttemptJson).
/// </summary>
public sealed record SynthesisJson(
    int SchemaVersion,
    string Mode,
    bool? Rotate180,
    double? J,
    double JMin,
    int? RefsAboveHalf,
    int? RefsTotal,
    int? RefsOffCrop,
    int NMin,
    string Verdict,
    string GateVerdict,
    bool Disagree,
    string? DisagreeChange);
```

Register on the source-gen context — add a new attribute to the existing `CalibrationBundleJsonContext` block:

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AttemptJson))]
[JsonSerializable(typeof(LocatorBestJson))]
[JsonSerializable(typeof(MapRectJson))]
[JsonSerializable(typeof(DetectionsJson))]
[JsonSerializable(typeof(RecoveredCalibrationJson))]
[JsonSerializable(typeof(SynthesisJson))]   // #1117
public partial class CalibrationBundleJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Run the tests to verify they pass.**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj --filter "FullyQualifiedName~SynthesisJsonRoundTripTests"
```

Expected: 2 passes.

- [ ] **Step 5: Run the full Capture test project to confirm no regressions.**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj
```

Expected: existing tests still pass (the existing bundle sink tests build their own `AttemptJson` snapshots indirectly via the sink — they read v3 bundles that have `synthesis: null` and should ignore the new field).

- [ ] **Step 6: Commit.**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/SynthesisJsonRoundTripTests.cs
git commit -m "$(cat <<'EOF'
feat(map-calibration): bundle schema 2→3 — add SynthesisJson + AttemptJson.Synthesis (#1117)

Additive schema bump. New SynthesisJson record (schemaVersion 1), new optional
Synthesis field on AttemptJson defaulted null. Source-gen context registers
the new record. Sink wiring lands in the next commit.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Wire bundle sink to write `Synthesis`

**Goal:** `FilesystemCalibrationAttemptBundleSink.WriteAttemptJson` translates `ctx.Result.Synthesis` into a `SynthesisJson` and writes it into `01-attempt.json`. Schema bump 2→3 takes effect on disk.

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`.
- Test: `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs` (extend).

- [ ] **Step 1: Write the failing tests.**

Append to `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs` (inside the existing `CalibrationAttemptBundleSinkTests` class). First, add a helper to build a Shadow-mode synthesis snapshot and attach it to the existing fixture:

```csharp
    // The existing CalibrationAttemptBundleSinkTests.cs file already imports
    // Mithril.MapCalibration.Detection, so SynthesisDiagnostics resolves unqualified.
    private static CalibrationAttemptContext PopulatedAcceptedWithShadowSynthesis()
    {
        var ctx = PopulatedAccepted();
        ctx.Result = ctx.Result! with
        {
            Synthesis = new SynthesisDiagnostics(
                Mode: "shadow",
                Rotate180: false,
                J: 7.5,
                JMin: 8.0,
                RefsAboveHalf: 6,
                RefsTotal: 11,
                RefsOffCrop: 2,
                NMin: 8,
                Verdict: "reject",
                GateVerdict: "accept",
                Disagree: true,
                DisagreeChange: "accept_to_reject"),
        };
        return ctx;
    }

    [Fact]
    public void V3_bundle_has_synthesis_section_when_synthesis_ran()
    {
        var ctx = PopulatedAcceptedWithShadowSynthesis();
        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var path = Path.Combine(dir, "01-attempt.json");
        using var fs = File.OpenRead(path);
        var parsed = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.AttemptJson);

        parsed.Should().NotBeNull();
        parsed!.SchemaVersion.Should().Be(3);
        parsed.Synthesis.Should().NotBeNull();
        parsed.Synthesis!.Mode.Should().Be("shadow");
        parsed.Synthesis.J.Should().Be(7.5);
        parsed.Synthesis.RefsAboveHalf.Should().Be(6);
        parsed.Synthesis.Verdict.Should().Be("reject");
        parsed.Synthesis.GateVerdict.Should().Be("accept");
        parsed.Synthesis.Disagree.Should().BeTrue();
        parsed.Synthesis.DisagreeChange.Should().Be("accept_to_reject");
    }

    [Fact]
    public void V3_bundle_omits_synthesis_when_mode_was_off()
    {
        // PopulatedAccepted leaves Result.Synthesis null — same as mode == Off.
        var ctx = PopulatedAccepted();
        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var path = Path.Combine(dir, "01-attempt.json");
        using var fs = File.OpenRead(path);
        var parsed = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.AttemptJson);

        parsed!.SchemaVersion.Should().Be(3);
        parsed.Synthesis.Should().BeNull();
    }
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj --filter "FullyQualifiedName~V3_bundle"
```

Expected: tests fail. The schemaVersion in the sink is hard-coded to 2 and Synthesis is never populated.

- [ ] **Step 3: Bump the sink's schemaVersion to 3 and populate Synthesis.**

In `src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`, modify the `WriteAttemptJson` method (currently around lines 240-258). Replace:

```csharp
var dto = new AttemptJson(
    SchemaVersion: 2,
    Area: ctx.Area,
    ...
    LocatorBest: ToLocatorBestJson(ctx));
```

with:

```csharp
var dto = new AttemptJson(
    SchemaVersion: 3,
    Area: ctx.Area,
    AttemptStartedUtc: ctx.StartedUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
    AttemptFinalizedUtc: finalized.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
    Outcome: ctx.Outcome,
    RejectReason: ctx.Result?.RejectReason ?? ctx.ExceptionInfo,
    EngineVersion: AssemblyVersion,
    Files: files,
    LocatorBest: ToLocatorBestJson(ctx),
    Synthesis: ToSynthesisJson(ctx.Result?.Synthesis));
```

Add a private helper right below `ToLocatorBestJson`. The sink file already imports `Mithril.MapCalibration.Detection` at line 11, so `SynthesisDiagnostics` resolves unqualified:

```csharp
// #1117: field-by-field translation from the engine-layer SynthesisDiagnostics
// to the bundle wire-format SynthesisJson. Null in → null out so pre-#1117
// solve results (or mode == Off) produce v3 bundles with synthesis: null.
private static SynthesisJson? ToSynthesisJson(SynthesisDiagnostics? d)
{
    if (d is null) return null;
    return new SynthesisJson(
        SchemaVersion: 1,
        Mode: d.Mode,
        Rotate180: d.Rotate180,
        J: d.J,
        JMin: d.JMin,
        RefsAboveHalf: d.RefsAboveHalf,
        RefsTotal: d.RefsTotal,
        RefsOffCrop: d.RefsOffCrop,
        NMin: d.NMin,
        Verdict: d.Verdict,
        GateVerdict: d.GateVerdict,
        Disagree: d.Disagree,
        DisagreeChange: d.DisagreeChange);
}
```

- [ ] **Step 4: Run the tests to verify they pass.**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj --filter "FullyQualifiedName~V3_bundle"
```

Expected: 2 passes.

- [ ] **Step 5: Run the full Capture test project to confirm no regressions.**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj
```

Expected: existing tests still pass (the existing `Writes_per_attempt_subdir_with_expected_name_on_accepted` and `Writes_all_11_files_on_accepted_attempt` are unaffected — they don't assert on `schemaVersion`).

- [ ] **Step 6: Commit.**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs
git commit -m "$(cat <<'EOF'
feat(map-calibration): bundle sink writes synthesis section into 01-attempt.json (#1117)

WriteAttemptJson now emits schemaVersion 3 and ToSynthesisJson translates the
engine-layer SynthesisDiagnostics field-by-field. Null in → null out so mode
== Off and pre-#1117 solve results produce v3 bundles with synthesis: null.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Forward-compat — v3 reader on pre-v3 bundle

**Goal:** Pin the contract that the post-#1117 code can read a bundle written by the pre-#1117 engine (no `synthesis` field present in the JSON). The default `Synthesis = null` on the v3 `AttemptJson` should make this work via `System.Text.Json` source-gen's default behavior — this test pins it so a future change to default-handling can't silently break old bundles users have on disk.

**Files:**
- Test: `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs` (extend).

- [ ] **Step 1: Write the failing forward-compat test.**

Append to `CalibrationAttemptBundleSinkTests`:

```csharp
    [Fact]
    public void V3_code_reads_pre_v3_bundle_with_null_synthesis()
    {
        // Hand-write a v2 bundle JSON (no `synthesis` field at all). This is exactly
        // what a pre-#1117 engine version wrote to disk; users may have these
        // bundles from before they updated.
        const string preV3Json = """
        {
          "schemaVersion": 2,
          "area": "Map_Test",
          "attemptStartedUtc": "2026-06-08T19:37:13.0000000Z",
          "attemptFinalizedUtc": "2026-06-08T19:37:14.0000000Z",
          "outcome": "accepted",
          "rejectReason": null,
          "engineVersion": "3.0.0.103+pre1117",
          "files": {
            "rawScreenshot": null,
            "grayScreenshot": null,
            "mapRect": null,
            "baseTextureResampled": null,
            "alignedScreenshot": null,
            "deviation": null,
            "detectionsImage": null,
            "projectionOverlay": null,
            "detections": null,
            "recoveredCalibration": null
          },
          "locatorBest": null
        }
        """;

        var parsed = JsonSerializer.Deserialize(preV3Json, CalibrationBundleJsonContext.Default.AttemptJson);

        parsed.Should().NotBeNull();
        parsed!.SchemaVersion.Should().Be(2);   // we preserve the on-disk value
        parsed.Area.Should().Be("Map_Test");
        parsed.Synthesis.Should().BeNull();     // missing field → default → null
    }
```

- [ ] **Step 2: Run the test to verify it passes immediately.**

```bash
dotnet test tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj --filter "FullyQualifiedName~V3_code_reads_pre_v3_bundle"
```

Expected: pass on first run (this is a contract pin — the default `Synthesis = null` on the v3 record already makes this work; no implementation needed).

- [ ] **Step 3: Commit.**

If the test passed without implementation:

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs
git commit -m "$(cat <<'EOF'
test(map-calibration): pin v3 reader's forward-compat with pre-v3 bundles (#1117)

Contract test: v3 AttemptJson record reads a pre-#1117 JSON snapshot (no
"synthesis" field) and lands Synthesis=null via the record's default. Locks
the upgrade story so user-collected bundles from before this PR keep parsing.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

If the test FAILS (unexpected — would indicate the default value wasn't picked up by source-gen), pause and investigate before continuing.

---

## Task 7: Shadow-mode Serilog `LogInformation` mirror

**Goal:** Emit one `Information` line per Shadow-mode solve attempt summarising the synthesis-J winner. Fires only in Shadow with a winner; skipped in Off and Enabled.

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` (`Solve` method, legacy branch).
- Test: `tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineLoggingTests.cs` (extend).

- [ ] **Step 1: Write the failing tests.**

Append to the existing `MapCalibrationSolveEngineLoggingTests` class (the existing `CapturingLogger`, `FixedDetector`, `AlwaysRejectGate` fixtures are reused):

```csharp
    [Fact]
    public void Shadow_mode_emits_synthesis_summary_line()
    {
        var (detector, refs, request) = BuildSynthesisFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Shadow };
        var logger = new CapturingLogger();
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger, options);

        engine.Solve(request, refs);

        logger.Infos.Should().ContainSingle(m => m.Contains("Synthesis-J (shadow"));
    }

    [Fact]
    public void Off_mode_emits_no_synthesis_line()
    {
        var (detector, refs, request) = BuildSynthesisFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Off };
        var logger = new CapturingLogger();
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger, options);

        engine.Solve(request, refs);

        logger.Infos.Should().NotContain(m => m.Contains("Synthesis-J"));
    }

    [Fact]
    public void Enabled_mode_does_not_double_log_synthesis()
    {
        var (detector, refs, request) = BuildSynthesisFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Enabled };
        var logger = new CapturingLogger();
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger, options);

        engine.Solve(request, refs);

        // The existing Enabled-mode line at lines 146-148/156 of MapCalibrationSolveEngine
        // already logs J in its own "Auto-calibration accepted/rejected (synthesis-J)"
        // message. The new Shadow-mode mirror MUST NOT also fire here.
        logger.Infos.Should().NotContain(m => m.Contains("Synthesis-J (shadow"));
    }

    [Fact]
    public void Shadow_mode_log_includes_disagree_property_when_gates_differ()
    {
        // The Hogan's case in miniature: legacy gate accepts a cal, synthesis-J would
        // reject (because J or RefsAboveHalf below the threshold). The "disagree=true"
        // signal is the bit threshold-tuning conversations want to grep on.
        var (detector, refs, request) = BuildSynthesisFixture();
        var options = new MapCalibrationSolverOptions
        {
            SynthesisRerankMode = SynthesisRerankMode.Shadow,
            // Force synthesis to reject by setting an unreachable Nmin floor.
            SynthesisNMin = 9999,
        };
        var logger = new CapturingLogger();
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysAcceptGate(), logger, options);

        engine.Solve(request, refs);

        var line = logger.Infos.Should().ContainSingle(m => m.Contains("Synthesis-J (shadow")).Subject;
        // The legacy gate accepted (AlwaysAcceptGate), synthesis would reject (Nmin=9999) → disagree.
        line.Should().Contain("disagrees-with-gate=True");
        line.Should().Contain("would-reject");
    }

    private static (ICalibrationDetector Detector, List<LandmarkReference> Refs, DetectionRequest Request) BuildSynthesisFixture()
    {
        var detections = new Dictionary<string, IReadOnlyList<TypedDetection>>(StringComparer.Ordinal)
        {
            ["Portal"] = new[] { new TypedDetection("Portal", "icon", new CroppedFramePixel(2, 2), 0.9) },
        };
        var detector = new FixedDetector(detections);
        var refs = new List<LandmarkReference>
        {
            new("Portal", "Test Portal", new WorldCoord(1, 0, 2)),
        };
        var img = new GrayImage(8, 8, new byte[64]);
        var rect = new MapRect(0, 0, 8, 8, 8, 8);
        var request = new DetectionRequest(img, img, rect, IconTemplateSet.Empty, RimMaskMode.None,
            LowNcc: 0.5, TypeFloor: 0.45,
            BlobOptions: new BlobOptions(MinArea: 8, MaxIconArea: 1500, MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5));
        return (detector, refs, request);
    }

    private sealed class AlwaysAcceptGate : ICalibrationConfidenceGate
    {
        public bool Accept(AreaCalibration solve, int inlierCount, out string? rejectReason)
        {
            rejectReason = null;
            return true;
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~MapCalibrationSolveEngineLoggingTests"
```

Expected: 4 new tests fail — the `Synthesis-J (shadow` line is not yet emitted.

- [ ] **Step 3: Emit the Shadow-mode line in `Solve`.**

In `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs`, find the `mode != Enabled` legacy branch in `Solve` (currently around lines 109-127). Right after the existing accept/reject `LogInformation` calls (`"Auto-calibration accepted: residual ... px, ... inliers."` / `"Auto-calibration rejected: ..."` / `LogInlierCorrespondences(...)`), insert:

```csharp
// #1117: Shadow-mode synthesis-J mirror. Fires only when synthesis ran AND produced
// a winner (mode == Shadow with bestSynthesis != null). Off skips synthesis entirely;
// Enabled's own accept/reject lines at 146-148 / 156 already log J. See spec D7.
if (mode == SynthesisRerankMode.Shadow && bestSynthesis is not null)
{
    var (synthVerdict, _, disagree, _) = ComputeVerdicts(bestSynthesis, legacyResult, mode);
    _logger?.LogInformation(
        "Synthesis-J (shadow, rotate180={Rotate180}): J={J:0.00} (min {Jmin:0.00}), "
        + "refs>=0.5 {Refs}/{Total} (min {Nmin}), off-crop {OffCrop}, "
        + "would-{Verdict}, disagrees-with-gate={Disagree}.",
        bestSynthesis.Rotate180,
        bestSynthesis.J, _options.SynthesisJMin,
        bestSynthesis.RefsAboveHalf, bestSynthesis.RefsTotal, _options.SynthesisNMin,
        bestSynthesis.RefsOffCrop,
        synthVerdict, disagree);
}
```

- [ ] **Step 4: Run the new tests to verify they pass.**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~MapCalibrationSolveEngineLoggingTests"
```

Expected: all 4 new tests pass + existing logging tests still pass.

- [ ] **Step 5: Run the full Detection test project to confirm no regressions.**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj
```

Expected: baseline + Task 1 (2) + Task 3 (3) + this task (4).

- [ ] **Step 6: Commit.**

```bash
git add src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineLoggingTests.cs
git commit -m "$(cat <<'EOF'
feat(map-calibration): Shadow-mode synthesis-J Serilog mirror (#1117)

Emit one Information line per Shadow-mode solve attempt:

  Synthesis-J (shadow, rotate180={Rotate180}): J={J:0.00} (min {Jmin:0.00}),
    refs>=0.5 {Refs}/{Total} (min {Nmin}), off-crop {OffCrop},
    would-{Verdict}, disagrees-with-gate={Disagree}.

Fires only when mode == Shadow AND a winner was found. Off skips synthesis;
Enabled already logs J in its own accept/reject line. Surfaces the
"disagree" bit a #1116-style investigator wants to grep on.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Full-suite sweep + manual smoke test

**Goal:** Run the whole test suite to confirm nothing else regressed, then sanity-check by capturing one real calibration attempt and inspecting both surfaces.

**Files:** none modified.

- [ ] **Step 1: Run the full solution test suite.**

```bash
dotnet test Mithril.slnx
```

Expected: all green.

- [ ] **Step 2 (optional but recommended): Manual smoke test.**

If the engineer has a working Mithril shell + Project Gorgon client:

1. Confirm `SynthesisRerankMode = Shadow` in the user's `%LocalAppData%/Mithril/Shell/shell.json` (or in code defaults — both should match the spec).
2. Launch Mithril:
   ```bash
   dotnet run --project src/Mithril.Shell
   ```
3. Trigger an auto-calibration in any area (Serbule is reliable; an indoor area like Hogan's Basement exercises the #1116 path).
4. After the attempt finishes, inspect:
   - **Serilog:** `%LocalAppData%/Mithril/Shell/logs/mithril-{yyyyMMdd}.json` — grep for `Synthesis-J (shadow` and confirm one line per solve attempt with the expected fields.
   - **Bundle:** `%LocalAppData%/Mithril/diagnostics/calibration/<Map_X>-<timestamp>-<outcome>/01-attempt.json` — confirm `schemaVersion: 3`, presence of `"synthesis": { … "mode": "shadow", "j": … }`.
5. If both surfaces look correct, proceed. If something is missing, debug before opening the PR.

- [ ] **Step 3: Confirm clean working tree.**

```bash
git status
```

Expected: clean (no uncommitted changes).

- [ ] **Step 4: Push branch + open PR.**

```bash
git push -u origin claude/1117-synthesis-j-observability
gh pr create --title "feat(map-calibration): Shadow-mode synthesis-J observability (#1117)" --body "$(cat <<'EOF'
## Summary

Adds two observability surfaces for the Shadow-mode synthesis-J score that were missing from the user-collected artifacts:

1. **Diagnostic bundle** `01-attempt.json` — schema bump v2→v3, new optional `synthesis` section carrying mode, rotate180, J, JMin, RefsAboveHalf/Total/OffCrop, NMin, verdict, gate-verdict, disagree flag, disagree-change.
2. **Per-day Serilog file** — one `Information` line per Shadow-mode solve:

```
Synthesis-J (shadow, rotate180=False): J=2.00 (min 8.00), refs>=0.5 1/4 (min 8),
  off-crop 0, would-reject, disagrees-with-gate=True.
```

The bundle is the primary investigator-facing surface (per-attempt, schema-versioned, shareable as a zip); the Serilog line is the secondary one for at-a-glance grep + `mithril-logs` MCP queryability.

Implementation lands as a series of TDD-ordered commits (one record + property, one helper extraction, one populate-step, one schema bump, one sink wiring, one forward-compat test pin, one logging emit). Each step is independently green; full plan: [`docs/planning/calibration-1117-synthesis-j-observability/plan.md`](docs/planning/calibration-1117-synthesis-j-observability/plan.md).

**No behavior change** — pure additive observability layer:
- Existing Meter histograms + ActivitySource span tags in `EmitSynthesisRerankTelemetry` are unchanged; they remain the OTLP / perf-trace source of truth.
- No threshold change, no `Shadow → Enabled` flip. That's [#1116](https://github.com/moumantai-gg/mithril/issues/1116) path 1, blocked on this issue producing measurable data first.

## Test plan

- [ ] `dotnet test Mithril.slnx` — all green
- [ ] `tests/Mithril.MapCalibration.Tests/Detection/SynthesisDiagnosticsTests.cs` — record + property defaults
- [ ] `tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineSynthesisWiringTests.cs` — Solve populates correctly across Shadow / Enabled / Off
- [ ] `tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineLoggingTests.cs` — Shadow emits, Off skips, Enabled doesn't double-log, disagree-true round-trip
- [ ] `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/SynthesisJsonRoundTripTests.cs` — wire-format round-trip + AttemptJson v3 emission
- [ ] `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs` — synthesis section present in Shadow, null in Off, v3 reader on pre-v3 bundle
- [ ] Manual: trigger a live auto-cal in Mithril; confirm `Synthesis-J (shadow` line in Serilog + `"synthesis"` field in `01-attempt.json`.

Closes #1117. Blocks resolution of #1116 path 1.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 5: Capture the PR URL** and surface it back to the user.

---

## Self-review checklist

Run this as a final pass before handing off:

1. **Spec coverage:**
   - ✅ Spec §3 D1 (two surfaces): Tasks 4-7 land both.
   - ✅ Spec §3 D2 (summary only, no per-ref): no plumbing through `ScoreOrientationCandidates` — only winner scalars consumed.
   - ✅ Spec §3 D3 (one emit per attempt, winner only): Task 7 fires once per Shadow attempt.
   - ✅ Spec §3 D4 (verbose threshold-bracketed template): Task 7 emit uses the exact template from the spec.
   - ✅ Spec §3 D5 (additive schema): Task 4 bumps `AttemptJson` to v3 with default-null `Synthesis`.
   - ✅ Spec §3 D6 (`SynthesisDiagnostics` → `SynthesisJson` wire mirror): Tasks 1 + 4 + 5.
   - ✅ Spec §3 D7 (engine emit fires in Shadow only): Task 7 gate `mode == Shadow && bestSynthesis is not null`.
   - ✅ Spec §3 D8 (`SynthesisDiagnostics` populated whenever synthesis ran): Task 3 `BuildSynthesisDiagnostics` returns non-null in Shadow + Enabled, null in Off.
   - ✅ Spec §3 D9 (no `docs/perf-trace-schema.md` edits): no doc edits in this plan.
   - ✅ Spec §2 in-scope `ComputeVerdicts` helper extraction: Task 2.
   - ✅ Spec §7.1 engine logging tests (4 cases): Task 7.
   - ✅ Spec §7.2 bundle JSON tests (4 cases): Tasks 4 (round-trip) + 5 (Shadow present + Off null + disagree round-trip) + 6 (forward-compat).
   - ✅ Spec §7.3 engine wiring test: Task 3.

2. **Placeholder scan:** no "TBD" / "TODO" / "add appropriate error handling" / "similar to Task N" phrases. All code blocks are complete and runnable.

3. **Type consistency:**
   - `SynthesisDiagnostics` field order matches `SynthesisJson` field order — verified by reading both records in Tasks 1 and 4.
   - `ComputeVerdicts` signature is consistent across Task 2 (definition), Task 3 (call from `BuildSynthesisDiagnostics`), and Task 7 (call from Shadow log emit).
   - `Disagree` is a bool everywhere; `DisagreeChange` is `string?` everywhere (with `"none"` only at the existing span-tag call site, preserved by the `change = changeOrNull ?? "none"` line in Task 2).

4. **Spec requirement → task mapping with no gaps:** confirmed above in (1).
