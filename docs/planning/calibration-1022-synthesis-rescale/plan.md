# Synthesis-J template rescale — implementation plan (#1022)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation` rescale icon templates to the on-screen render size before sliding NCC, eliminating the 50–75 s synthesis-J solve regression introduced in #999.

**Architecture:** Two-line plumbing change inside `Mithril.MapCalibration.Detection`: thread `TypeFloor` + `RenderSizePx` from `DetectionRequest` into the field builder, then call `IconRenderScaler.RenderSized` exactly the way `DeviationBlobCalibrationDetector` already does. One new regression test in the existing `SynthesisRerankFieldEquivalenceTests` file closes the synthetic-fixture blind spot.

**Tech Stack:** .NET 10, C# 13, xunit + FluentAssertions, span-based BCL-only image ops. No new dependencies.

**Spec:** [spec.md](./spec.md)

---

## File map

- **Modify** `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` — flip `private static` → `internal static` on `BuildLikelihoodFieldsFromDeviation`, add two parameters, rescale templates, update the one callsite.
- **Modify** `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs` — add one new `[Fact]` to the existing class.

No new files. No csproj edits (the test project already has `InternalsVisibleTo` via [Mithril.MapCalibration.Detection.csproj:23](../../../src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj)).

---

## Task 1: Plumb new parameters through (no behavior change)

Adds `typeFloor` + `renderSizePx` parameters to `BuildLikelihoodFieldsFromDeviation`, threads them from the callsite, flips visibility to `internal static`. The method body doesn't use the new parameters yet — this commit is pure API plumbing.

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` (callsite at line 94, method signature at lines 351–354)

- [ ] **Step 1: Update the callsite at line 94**

Find the existing line:

```csharp
var fields = BuildLikelihoodFieldsFromDeviation(req.Screenshot, req.BaseTexture, req.Templates);
```

Replace with:

```csharp
var fields = BuildLikelihoodFieldsFromDeviation(
    req.Screenshot, req.BaseTexture, req.Templates,
    req.TypeFloor, req.RenderSizePx);
```

- [ ] **Step 2: Update the method signature at line 351**

Find the existing signature:

```csharp
private static IReadOnlyDictionary<string, double[,]> BuildLikelihoodFieldsFromDeviation(
    GrayImage screenshot,
    GrayImage baseTexture,
    IconTemplateSet templates)
```

Replace with:

```csharp
internal static IReadOnlyDictionary<string, double[,]> BuildLikelihoodFieldsFromDeviation(
    GrayImage screenshot,
    GrayImage baseTexture,
    IconTemplateSet templates,
    double typeFloor,
    int? renderSizePx)
```

The method body is unchanged in this task — `typeFloor` and `renderSizePx` are accepted but unused. Behavior is byte-identical to before.

- [ ] **Step 3: Build the affected project**

Run from the worktree root:

```powershell
dotnet build src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj
```

Expected: build succeeds (single project, ~1–3 s on a warm machine; first build of the worktree may take longer).

- [ ] **Step 4: Run the detection test project to confirm no regressions**

```powershell
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj
```

Expected: all tests pass. `SynthesisRerankFieldEquivalenceTests`, `SynthesisRerankShadowModeTests`, `SyntheticLargeTemplateEndToEndTests`, and `ReplayFixtureTests` continue to pass — the new parameters are accepted but unused, so behavior hasn't moved.

- [ ] **Step 5: Commit**

```powershell
git add src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs
git commit -m "refactor(map-calibration): plumb TypeFloor + RenderSizePx into synthesis L_t builder

No behavior change — adds two parameters to BuildLikelihoodFieldsFromDeviation
and threads them from the single callsite. Sets up Task 3's rescale fix for #1022.

Method visibility flips private → internal so a regression test can call it
directly (InternalsVisibleTo to Mithril.MapCalibration.Tests is already wired).

Refs: #1022"
```

---

## Task 2: Add the failing regression test (RED)

Adds a `[Fact]` to the existing `SynthesisRerankFieldEquivalenceTests` class that calls `BuildLikelihoodFieldsFromDeviation` with a PG-native-resolution template and asserts byte-equivalence against a manually-rescaled reference. With the bug still present (Task 3 not yet applied), the production path skips the rescale, the L_t field comes out degenerate, and the assertion fires.

**Files:**
- Modify: `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs`

- [ ] **Step 1: Add the new `[Fact]` to the existing class**

Open `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs` and insert the following method after the closing brace of the existing `Production_path_and_probe_LoadDeviationAsField_produce_identical_fields` test (before the class's closing brace):

```csharp
    /// <summary>
    /// PG ships icon sprites at native resolution (~256 px) but renders map icons at
    /// ~16 px on-screen. Production must rescale templates before sliding NCC, or
    /// L_t comes out mostly-zero (mithril#1022). With a native-res template + a
    /// pinned RenderSizePx, the production path's L_t must equal the field built
    /// from the same template manually rescaled to the same render size.
    /// </summary>
    [Fact]
    public void Production_rescales_native_resolution_templates_before_scoring()
    {
        const int W = 600, H = 400;
        const int RenderSizePx = 16;

        var texturePixels = SyntheticMap.MakeTexture(W, H, seed: 1022);
        var shotPixels = (byte[])texturePixels.Clone();
        // A few icon-shaped bright spots in the screenshot so the deviation has
        // predictable signal across the field.
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 120, anchorY: 90,  width: 16, height: 16, luminance: 220);
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 320, anchorY: 200, width: 16, height: 16, luminance: 220);
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 480, anchorY: 310, width: 16, height: 16, luminance: 220);

        var shot = new GrayImage(W, H, shotPixels);
        var tex  = new GrayImage(W, H, texturePixels);

        // Native-resolution template: 256x245 (matches the user's icon cache shape
        // for landmark_telepad in #1022). Exceeds IconRenderScaler.ScaleSearchThresholdPx = 64,
        // so RenderSized engages and (with pinnedSize) deterministically rescales to 16 px.
        const int NativeW = 256, NativeH = 245;
        var grayBytes  = SyntheticMap.MakeTexture(NativeW, NativeH, seed: 4242);
        var alphaBytes = new byte[NativeW * NativeH];
        for (int i = 0; i < alphaBytes.Length; i++) alphaBytes[i] = 255;
        var nativeTemplate = new IconTemplate(
            Name: "landmark_telepad_native",
            LandmarkType: "TeleportationPlatform",
            PivotX: 0.5, PivotY: 0.5,
            Gray:  new GrayImage(NativeW, NativeH, grayBytes),
            Alpha: new GrayImage(NativeW, NativeH, alphaBytes));
        var templates = new IconTemplateSet([nativeTemplate]);

        // Path A — production path under test.
        var prodFields = MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation(
            shot, tex, templates,
            typeFloor: 0.0,
            renderSizePx: RenderSizePx);
        prodFields.Should().ContainKey("TeleportationPlatform");
        var prodField = prodFields["TeleportationPlatform"];

        // Path B — manual rescale + LoadDeviationAsField (mirrors the probe path).
        var rescaled = IconRenderScaler.RenderSized(shot, templates.Templates, threshold: 0.0, pinnedSize: RenderSizePx);
        rescaled.Should().HaveCount(1);
        var refTemplate = rescaled[0];

        var devBytes = new byte[W * H];
        for (int i = 0; i < devBytes.Length; i++)
        {
            int d = shot.Pixels[i] - tex.Pixels[i];
            devBytes[i] = d > 0 ? (byte)System.Math.Min(255, d) : (byte)0;
        }
        var devImage = new GrayImage(W, H, devBytes);
        var refField = IconLikelihoodField.LoadDeviationAsField(
            devImage, refTemplate,
            applyRimMask: true,
            devThr: IconLikelihoodField.DefaultDevThr);

        // Byte-equivalent.
        prodField.GetLength(0).Should().Be(refField.GetLength(0));
        prodField.GetLength(1).Should().Be(refField.GetLength(1));
        for (int y = 0; y < prodField.GetLength(0); y++)
        for (int x = 0; x < prodField.GetLength(1); x++)
        {
            prodField[y, x].Should().Be(refField[y, x],
                $"production path must rescale native-res templates so L_t matches a rescaled-template build at ({x},{y})");
        }
    }
```

- [ ] **Step 2: Run the new test only to confirm it fails (RED)**

```powershell
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~SynthesisRerankFieldEquivalenceTests.Production_rescales_native_resolution_templates_before_scoring"
```

Expected: **FAIL**. The production path doesn't rescale yet (Task 1 only added the parameter without using it), so `prodField` is built against the 256×245 native template — `ScoreAll` border-skip zeros most positions — while `refField` is built against the 16×15 rescaled template. The per-cell `.Should().Be(...)` assertion fires within the first non-matching pixel.

If the test instead errors with a compile or runtime exception, fix the test (do not fall through to Task 3). Specifically, watch for:
- `MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation` not visible — confirms Task 1 step 2 didn't flip visibility; redo it.
- `IconRenderScaler.RenderSized` signature mismatch — confirms `(GrayImage, IReadOnlyList<IconTemplate>, double, int?)` is correct.

- [ ] **Step 3: Confirm existing tests still pass (no collateral damage)**

```powershell
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~SynthesisRerankFieldEquivalenceTests.Production_path_and_probe_LoadDeviationAsField_produce_identical_fields"
```

Expected: PASS. The existing test uses `SyntheticMap.DefaultIcons` (≤64 px), which short-circuits `RenderSized`, so behavior is unchanged.

- [ ] **Step 4: Commit (RED checkpoint)**

```powershell
git add tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs
git commit -m "test(map-calibration): regression test for #1022 (RED)

Asserts production's L_t field equals the rescaled-template L_t when a
native-resolution (~256 px) template is passed in. Closes the SyntheticMap.DefaultIcons
blind spot — small fixtures short-circuit IconRenderScaler.ScaleSearchThresholdPx
so the bug never manifested in the existing suite.

Expected to fail at this commit; Task 3 will turn it green.

Refs: #1022"
```

---

## Task 3: Implement the rescale (GREEN)

Wraps `templates.Templates` in `IconRenderScaler.RenderSized` before the per-type dedup loop. Mirrors the detector's call shape at [DeviationBlobCalibrationDetector.cs:52](../../../src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs). Turns Task 2's red test green.

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` (the method body around lines 374–388)

- [ ] **Step 1: Replace the dedup-loop input with a rescaled list**

Find the existing block inside `BuildLikelihoodFieldsFromDeviation`:

```csharp
    // One template per landmark-type — the per-type L_t fields are keyed by
    // LandmarkType. If a type has multiple templates (e.g. variants), the
    // LAST in iteration order wins, matching the probe's path at
    // SynthesisProbePhase.cs (`fieldsByType[template.LandmarkType] = ...`
    // inside a foreach). Production must match this so Task 17's L_t equality
    // test holds in any future multi-template-per-type scenario.
    var perType = new Dictionary<string, IconTemplate>(StringComparer.Ordinal);
    foreach (var template in templates.Templates)
    {
        perType[template.LandmarkType] = template;
    }
```

Replace with:

```csharp
    // PG ships icon sprites at native resolution (~256 px) but renders map icons
    // at a single small on-screen size (~16 px). Single-scale NCC only correlates
    // at matching size, so the templates MUST be downscaled to the render size
    // before sliding — otherwise every native-res template is larger than its
    // viable search area and produces a mostly-zero L_t (mithril#1022). Mirrors
    // DeviationBlobCalibrationDetector.cs:52. Returns templates unchanged when
    // they're already small (the synthetic-fixture path).
    var rescaled = IconRenderScaler.RenderSized(screenshot, templates.Templates, typeFloor, renderSizePx);

    // One template per landmark-type — the per-type L_t fields are keyed by
    // LandmarkType. If a type has multiple templates (e.g. variants), the
    // LAST in iteration order wins, matching the probe's path at
    // SynthesisProbePhase.cs (`fieldsByType[template.LandmarkType] = ...`
    // inside a foreach). Production must match this so Task 17's L_t equality
    // test holds in any future multi-template-per-type scenario.
    var perType = new Dictionary<string, IconTemplate>(StringComparer.Ordinal);
    foreach (var template in rescaled)
    {
        perType[template.LandmarkType] = template;
    }
```

Two changes: a new `var rescaled = IconRenderScaler.RenderSized(...)` line above the dedup, and the `foreach` iterates `rescaled` instead of `templates.Templates`.

- [ ] **Step 2: Run the new regression test — confirm GREEN**

```powershell
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj --filter "FullyQualifiedName~SynthesisRerankFieldEquivalenceTests.Production_rescales_native_resolution_templates_before_scoring"
```

Expected: PASS. Both `prodField` and `refField` are now built against the same 16×15 rescaled template, so the per-cell equality holds.

- [ ] **Step 3: Run the full detection test project**

```powershell
dotnet test tests/Mithril.MapCalibration.Tests/Mithril.MapCalibration.Tests.csproj
```

Expected: all tests pass. Pay particular attention to:
- `SynthesisRerankFieldEquivalenceTests` (both facts): green.
- `SynthesisRerankShadowModeTests`: green — the shadow-mode telemetry assertions don't depend on field magnitudes, only on span emission + legacy verdict parity, both of which are unaffected.
- `SyntheticLargeTemplateEndToEndTests`: green — its `RenderSizePx = 32` test path was already exercising `IconRenderScaler` via the detector; the synthesis-J side now matches.
- `ReplayFixtureTests`: green — uses `RenderSizePx = 16` on real-asset replay; the fields were degenerate before, are now meaningful, but the test asserts the solver's *output* (calibration / inlier count), not L_t shape, so verdict parity holds.

- [ ] **Step 4: Run the full solution test suite**

```powershell
dotnet test Mithril.slnx
```

Expected: all tests pass across all projects. Watch for any solver-consuming test in `Mithril.MapCalibration.Capture.Tests` or `Legolas.Tests` that asserts a synthesis-J field magnitude (none expected, but the broad sweep catches surprises).

- [ ] **Step 5: Commit (GREEN)**

```powershell
git add src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs
git commit -m "fix(map-calibration): rescale templates in synthesis-J L_t builder (#1022)

BuildLikelihoodFieldsFromDeviation now calls IconRenderScaler.RenderSized
on its input templates before the per-type dedup, mirroring the call shape
at DeviationBlobCalibrationDetector.cs:52. Production and detector now
score against identical template inputs.

Before: per-attempt solve cost 50–75 s on Eltibule (Player.log
'solve finished in 75303 ms'); L_t fields were mostly zero because
native-res templates fell off the deviation crop at almost every
sliding-NCC position.

After: per-attempt solve is bounded by 16 px sliding NCC × 4 templates ×
2 orientations ≈ 200 ms. L_t carries real signal, so the synthesis-J
Shadow-mode telemetry from #999 becomes a meaningful signal that can
inform a future Shadow → Enabled flip.

Closes #1022.
Refs: #999, #916."
```

---

## Task 4: Verify on the live capture loop, open PR

The unit-test regression closes the synthetic blind spot; the live capture loop is the acceptance criterion the issue called out (`solve finished in {Elapsed} ms` < 1 s).

- [ ] **Step 1: Confirm Mithril is not running (file-lock guard)**

```powershell
Get-Process Mithril.Shell -ErrorAction SilentlyContinue
```

Expected: no output (no process running). If a Mithril.Shell process is reported, close it before continuing — otherwise the next build step's DLL output can be silently stale (memory note `mithril_build_file_lock_silent`, hook `check-mithril-running.ps1`).

- [ ] **Step 2: Verify the live capture-loop log line**

The issue authored a comparison table from `%LocalAppData%/Mithril/Shell/logs/mithril-2026060{2,3}.json` showing `solve finished in {Elapsed} ms` at ~50–75 s. A post-fix capture should drop this under 1 s.

The user runs this manually (the test bench is their machine, not a CI environment). Note in the PR body that the metric is reproducible by:
1. Build + launch the shell (`scripts/start.ps1`).
2. Trigger an auto-calibration attempt in Eltibule (the same area as the issue body's runs).
3. Open the day's `%LocalAppData%/Mithril/Shell/logs/mithril-YYYYMMDD.json` and search for `solve finished in`.

Defer the local verification to the user — don't fabricate numbers.

- [ ] **Step 3: Push the branch and open the PR**

The current branch is `claude/frosty-sinoussi-e00eb6`. Push and open with the body written to a temp file (`gh --body` with a bash heredoc swallows quotes — memory `bash_tool_is_posix_not_powershell`, use `--body-file` instead):

```powershell
git push -u origin claude/frosty-sinoussi-e00eb6
$body = @'
## Summary

Closes #1022.

`MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation` (lifted in #999) was passing PG-native-resolution icon templates (~235–256 px) straight into `IconLikelihoodField.LoadDeviationAsField`, skipping the `IconRenderScaler.RenderSized` step the detector path already uses. Two consequences on every auto-calibration attempt:

- **Cost.** Sliding 256 × 245 templates over a ~621 × 617 deviation crop is ~50 B ops × 4 templates × 2 orientations ≈ ~50–75 s per solve (observed: 75303 ms on Eltibule, see issue body).
- **Signal.** `ScoreAll`'s border-skip zeros every position where the template wouldn't fit, so a 256 px template against a ~621 px crop produces a mostly-zero L_t — the synthesis-J telemetry being accumulated under Phase C of #999 was being scored on degenerate fields.

The fix mirrors the call shape at `DeviationBlobCalibrationDetector.cs:52`: rescale templates via `IconRenderScaler.RenderSized(screenshot, templates, typeFloor, renderSizePx)` before the per-type dedup. `typeFloor` and `renderSizePx` come from the same `DetectionRequest` the detector already reads from.

## Changes

- `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` — `BuildLikelihoodFieldsFromDeviation` now takes `typeFloor` + `renderSizePx`, rescales templates, and is `internal` for test access.
- `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs` — new `[Fact]` asserts production's L_t equals a manually-rescaled-template L_t when fed a native-resolution template. The existing `SyntheticMap.DefaultIcons` test stays untouched; small fixtures short-circuit `IconRenderScaler.ScaleSearchThresholdPx = 64`, which is why this blind spot existed.

## Verification

- `dotnet test Mithril.slnx` green.
- The new regression test fails on `main` (cherry-pick to verify), passes here.
- Live bench (Eltibule auto-calibration attempt, `%LocalAppData%/Mithril/Shell/logs/mithril-YYYYMMDD.json` → `solve finished in {Elapsed} ms`) — owed by the human reviewer; expected to drop from ~50–75 s to <1 s. See [spec.md](docs/planning/calibration-1022-synthesis-rescale/spec.md) for the acceptance criterion.

## Out of scope

- Re-baselining `MapCalibrationSolverOptions.JMin` / `NMin` defaults — defer to a follow-up if Phase-C telemetry post-fix shows drift.
- Hoisting the rescale to `Solve(...)` entry across both orientations — bounded duplicate work (`RenderSized` short-circuits once templates ≤16 px), not worth the broader change for a regression fix.

— drafted by Claude (Opus 4.7), posted by @arthur-conde
'@
$tempFile = Join-Path $env:TEMP "pr-1022-body-$(Get-Date -Format 'yyyyMMddHHmmss').md"
$body | Out-File -Encoding utf8 $tempFile
gh pr create --base main --head claude/frosty-sinoussi-e00eb6 `
    --title "fix(map-calibration): rescale templates in synthesis-J L_t builder (#1022)" `
    --body-file $tempFile
Remove-Item $tempFile
```

Expected: `gh pr create` returns the PR URL. Capture it for Task 4 step 4 and end-of-session summary.

- [ ] **Step 4: Mark the planning row shipped after merge (separate commit on main)**

After the PR merges, flip `docs/planning/INDEX.md`:

```diff
- | [calibration-1022-synthesis-rescale](calibration-1022-synthesis-rescale/) | active | [#1022](https://github.com/moumantai-gg/mithril/issues/1022) | …
+ | [calibration-1022-synthesis-rescale](calibration-1022-synthesis-rescale/) | shipped | [#1022](https://github.com/moumantai-gg/mithril/issues/1022) · [#<merged-PR>](https://github.com/moumantai-gg/mithril/pull/<merged-PR>) | …
```

This is a tiny follow-up commit on `main` (or include in the same PR before merge if convenient). Either way, the row's `active` → `shipped` flip is non-negotiable per the planning index convention.

---

## Definition of done

- All three tasks committed on `claude/frosty-sinoussi-e00eb6`.
- `dotnet test Mithril.slnx` green.
- PR opened against `main`.
- User has (or has been asked to) confirm the live `solve finished in {Elapsed} ms` log line drops under 1 s on the Eltibule test bench.
- Planning row flipped to `shipped` after merge.

## Out of scope (per spec)

- Re-baselining `MapCalibrationSolverOptions.JMin` / `NMin` defaults — follow-up issue if Phase-C telemetry shows drift post-fix.
- Hoisting the rescale to `Solve(...)` entry across both orientations — bounded duplicate work, not worth the broader change.
- Runtime fail-loud assertion that L_t is non-degenerate — fixture-coupled; covered at the unit-test layer.
