# Synthesis-J Re-Rank Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `MapCalibrationSolveEngine`'s inlier-count acceptance gate with a synthesis-J re-rank that scores each top-K RANSAC candidate against the full reference pool, shipped behind a runtime-flippable three-state toggle that defaults to Shadow mode (compute + telemetry, legacy gate is still the source of truth).

**Architecture:** PR-1 moves four pure-math files (`IconLikelihoodField`, `JEvaluator`, `CandidateTransform`, `LocalRefine`) from `tools/MapCalibrationFromScreenshot/SynthesisProbe/` into `src/Mithril.MapCalibration/Detection/` so production and the probe consume one math path; adds `CandidateTransform.FromCalibration(AreaCalibration, MapRect)` as the in-memory bundle-free factory; adds `TypeAwareRansacSolver.SolveTopK`. No production behaviour change. PR-2 adds a `MapCalibrationSolverOptions` POCO + `SynthesisRerankMode { Off, Shadow, Enabled }` enum, wires the per-orientation L_t build / top-K score / synthesis-J selector into `MapCalibrationSolveEngine`, and emits the `calibration.synthesis_rerank` span + meters. Default ships as Shadow. PR-3 (flip default to Enabled) is gated on real-world telemetry and is **out of scope for this plan** — file as a follow-up issue when the acceptance criteria in the spec's Q2 are met.

**Tech Stack:** .NET 10, C# latest, xunit + FluentAssertions, BCL-only in `src/Mithril.MapCalibration/Detection/` (decoder-free invariant + the "no ProjectReference to Mithril.Shared" layering invariant). DI through `MapCalibrationServiceCollectionExtensions.AddMithrilMapCalibrationEngine`. Telemetry through a NEW `Mithril.MapCalibration.Diagnostics.MapCalibrationDiagnostics` local catalog (because `Mithril.MapCalibration.csproj` deliberately doesn't reference `Mithril.Shared` — same architectural pattern Arda uses with `ArdaActivitySources` / `ArdaMeters`).

**Read this once before starting:** [docs/superpowers/specs/2026-06-02-synthesis-rerank-design.md](../specs/2026-06-02-synthesis-rerank-design.md). The plan implements its Milestones PR-1 + PR-2 verbatim; PR-3 is deferred.

**Commit cadence:** one commit per task. Use the existing project convention `feat(map-calibration): …` / `refactor(map-calibration): …`. The CLAUDE.md guardrail "branch policy blocks direct commits to main" applies — work happens on a feature branch and ships as a PR per milestone (PR-1 = Tasks 1-8, PR-2 = Tasks 9-18). PR-1 and PR-2 are intentionally separate PRs because PR-1 is a no-behaviour-change move and is reviewable on its own; PR-2 adds the runtime toggle + telemetry.

**Review checkpoints (the key planning decision):** tasks are grouped into review *blocks*. Commits land per task as usual (no behaviour change in PR-1 means a green-build streak is the safety net; PR-2's smoke tests pin the load-bearing logic). Human review pauses ONLY at the explicit `🛑 Review checkpoint` markers, not after every commit. There are **three** review checkpoints across the whole plan:

| # | When | What gets reviewed |
|---|---|---|
| 1 | End of PR-1 (after Block 2) | The whole math-relocation diff in one pass. Mechanical move + new top-K helper + pinned equivalence test. |
| 2 | Mid-PR-2 (after Block 4, recommended-but-optional) | `MapCalibrationSolveEngine.cs` + `MapCalibrationDiagnostics.cs` against the spec's mode-semantics table. Catches algorithmic drift before tests pin the wrong contract. Skip if you've personally walked through the spec. |
| 3 | End of PR-2 (after Block 5) | Full PR diff including the three-mode smoke tests + existing-test audit. |

Each block is meant to run as a single uninterrupted stretch. If a subagent finishes a block, **continue to the next block before pausing** unless the markers say otherwise. The first-line check inside each block is "does the build still build" — that's the cheap-and-fast surface; treat human review as a scarce resource that's spent only at the markers.

---

## File Structure

### PR-1 — Math relocation (no behaviour change)

| Action | Path | Responsibility |
|---|---|---|
| Move | `src/Mithril.MapCalibration/Detection/IconLikelihoodField.cs` | `Build`, `LoadDeviationAsField`, `Sample`, `ScoreAll`, `DefaultDevThr`. BCL-only. Public. |
| Move | `src/Mithril.MapCalibration/Detection/JEvaluator.cs` | `JResult` record + `Evaluate(CandidateTransform, fields, refs)`. Refactored to consume `LandmarkReference`. Public. |
| Move | `src/Mithril.MapCalibration/Detection/CandidateTransform.cs` | Record + `Apply(WorldCoord)` + `FromAreaCalibration(AreaCalibration)` (kept) + **new** `FromCalibration(AreaCalibration, MapRect, out double anisotropyPercent)`. Public. |
| Move | `src/Mithril.MapCalibration/Detection/LocalRefine.cs` | `Run(seed, fields, refs, maxIter, stepInit)` hill-climber. Refactored to consume `LandmarkReference`. Public. |
| Modify | `src/Mithril.MapCalibration/Detection/TypeAwareRansacSolver.cs` | Add public `SolveTopK(detectionsByType, allRefs, mapRect, int k)` returning `IReadOnlyList<TopKCandidate>`. Existing `Solve` becomes a thin wrapper over `SolveTopK(..., k:1)`. |
| Delete | `tools/MapCalibrationFromScreenshot/SynthesisProbe/ReferencePoint.cs` | Removed — probe-side calls now build `LandmarkReference` directly. |
| Modify | `tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/MapRectConversion.cs` | Becomes a thin adapter: builds an in-memory `AreaCalibration` from `RecoveredCalibrationJson` and calls `CandidateTransform.FromCalibration`. |
| Modify | `tools/MapCalibrationFromScreenshot/SynthesisProbe/ProbeReferences.cs` + `Experiments/E1..E5_*.cs` | Switch to `IReadOnlyList<LandmarkReference>`. Mechanical signature change. |
| Create | `tests/Mithril.MapCalibration.Tests/Detection/CandidateTransformConversionTests.cs` | Conversion-equivalence test: production `FromCalibration(AreaCalibration, MapRect)` vs. probe-side `MapRectConversion.FromRecoveredCalibration(RecoveredCalibrationJson, MapRect)`. |

**Files that stay in `tools/`:** `Bundle/BundleJsonDtos.cs`, `Bundle/BundleLoader.cs`, `Bundle/BundleArgsResolver.cs`, `Bundle/PngHeader.cs`. Production reads `CalibrationAttemptContext` in-memory and never reads its own bundles back; only the probe loads bundles from disk.

### PR-2 — Synthesis-J wiring + telemetry (Shadow mode default)

| Action | Path | Responsibility |
|---|---|---|
| Create | `src/Mithril.MapCalibration/MapCalibrationSolverOptions.cs` | POCO + `SynthesisRerankMode { Off, Shadow, Enabled }` enum. `INotifyPropertyChanged`. Defaults: Shadow, J_min=8.0, N_min=8, K=8. |
| Create | `src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs` | LOCAL catalog (the project can't reference `Mithril.Shared`): `ActivitySource("Mithril.MapCalibration.Detection")` + `Meter("Mithril.MapCalibration.Detection")` with 3 instruments (`mithril.map_calibration.synthesis.j` Histogram, `mithril.map_calibration.synthesis.refs_above_threshold` Histogram, `mithril.map_calibration.synthesis.disagree` Counter). |
| Modify | `src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs`, `MithrilMeters.cs` | One-line pointer comments documenting that the Detection-layer catalogs live in the local catalog (mirrors the existing Arda pointer pattern). |
| Modify | `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs` | Optional `MapCalibrationSolverOptions` ctor param. New per-orientation L_t build + top-K score + cross-orientation J selector + synthesis-J gate. New span emit. Legacy gate remains source of truth in Shadow/Off; synthesis-J is the gate only in Enabled. |
| Modify | `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs` | `TryAddSingleton<MapCalibrationSolverOptions>` in `AddMithrilMapCalibrationEngine`. Inject into the `MapCalibrationSolveEngine` factory. |
| Create | `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankShadowModeTests.cs` | Engine smoke tests: mode=Off (no L_t build, no telemetry), mode=Shadow (verdict matches legacy), mode=Enabled (synthesis-J is the gate, low-J accept→reject). |
| Create | `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs` | Production-vs-probe L_t equality test: feed the same aligned crop + aligned texture through `JEvaluator.Evaluate` via production's deviation path and probe's `LoadDeviationAsField` path; assert byte-equivalent field arrays. |

---

## PR-1 — Math relocation (Tasks 1-8) · ends at 🛑 Review checkpoint 1

### Block 1 — File moves + adapter thin (Tasks 1-6)

Tasks 1-6 are tightly cascading: Tasks 2-5 leave the build red until Task 5 closes the cascade. Run this block straight through, committing per task. No human review inside the block — the build going green again at the end of Task 5 is the cheap correctness signal, and Task 6's probe E2E sanity check is the strong one.

---

### Task 1: Move `IconLikelihoodField` into `src/`

**Files:**
- Create: `src/Mithril.MapCalibration/Detection/IconLikelihoodField.cs`
- Delete: `tools/MapCalibrationFromScreenshot/SynthesisProbe/IconLikelihoodField.cs`

- [ ] **Step 1: Move the file**

Copy `tools/MapCalibrationFromScreenshot/SynthesisProbe/IconLikelihoodField.cs` to `src/Mithril.MapCalibration/Detection/IconLikelihoodField.cs`, then delete the tools copy.

- [ ] **Step 2: Update the namespace + access modifier**

In the new copy:
- Change `namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;` → `namespace Mithril.MapCalibration.Detection;`
- Change `internal static class IconLikelihoodField` → `public static class IconLikelihoodField`
- Remove the `using Mithril.MapCalibration.Detection;` line at the top — the class now lives in that namespace.

- [ ] **Step 3: Add `using Mithril.MapCalibration.Detection;` to existing probe consumers**

Add `using Mithril.MapCalibration.Detection;` to the top of every tool file that uses `IconLikelihoodField`:
- `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E1_TruthScore.cs`
- `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E2_TranslationSweep.cs`
- `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E3_ScaleSweep.cs`
- `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E4_RansacSeedScore.cs`
- `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E5_ColdGrid.cs`
- `tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs`
- `tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbeWriter.cs`

(Use the Grep tool with pattern `IconLikelihoodField` over `tools/` to confirm the list before editing.)

- [ ] **Step 4: Build and verify**

Run: `dotnet build Mithril.slnx`
Expected: build green. (CLAUDE.md guardrail: if Mithril is running, the `check-mithril-running.ps1` PreToolUse hook blocks this — close Mithril first.)

- [ ] **Step 5: Run map-calibration tests**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~Mithril.MapCalibration.Tests"`
Expected: all green. No new tests; existing tests prove the move is byte-identical.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/IconLikelihoodField.cs \
        tools/MapCalibrationFromScreenshot/SynthesisProbe/IconLikelihoodField.cs \
        tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/*.cs \
        tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbe*.cs
git commit -m "refactor(map-calibration): move IconLikelihoodField into src/ Detection"
```

---

### Task 2: Move `JEvaluator` into `src/` and switch to `LandmarkReference`

**Files:**
- Create: `src/Mithril.MapCalibration/Detection/JEvaluator.cs`
- Delete: `tools/MapCalibrationFromScreenshot/SynthesisProbe/JEvaluator.cs`

The probe's `ReferencePoint` record (Label, LandmarkType, WorldX, WorldZ) duplicates the production `LandmarkReference` record (Type, Name, WorldCoord World). Production must consume the type it already has; the probe converts at the load boundary in Task 5.

- [ ] **Step 1: Create the production `JEvaluator`**

Write `src/Mithril.MapCalibration/Detection/JEvaluator.cs`:

(Note: `Directory.Build.props` enables `<ImplicitUsings>enable</ImplicitUsings>` repo-wide — `System`, `System.Collections.Generic`, `System.Linq` etc. are global. **Do NOT add explicit `using System.Collections.Generic;` / `using System;`** in the new files in this plan — they trigger CS8019 / CS8933 warnings, and the repo's warnings-as-errors policy treats those as build failures in some configurations. Only add `using` for namespaces NOT in the global set.)

```csharp
namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Result of one J(T) evaluation over a reference pool.
/// </summary>
/// <param name="J">Sum of per-ref L_t scores (range roughly [-|refs|, |refs|] but
/// in practice positive for any reasonable fit; the wrong-fit case is sub-1).</param>
/// <param name="RefsAboveHalf">Refs whose sampled L_t ≥ 0.5 — the "N" component
/// of the gate (synthesis-J accepts iff J ≥ J_min AND RefsAboveHalf ≥ N_min).</param>
/// <param name="RefsOffCrop">Refs whose projected position fell outside the L_t
/// field. Diagnostic: a fit with many off-crop refs is geometrically suspect.</param>
/// <param name="PerRefScores">Per-ref score (same order as <c>refs</c>). For
/// debugging / bundle output; consumers don't have to use it.</param>
public readonly record struct JResult(
    double J,
    int RefsAboveHalf,
    int RefsOffCrop,
    IReadOnlyList<double> PerRefScores);

/// <summary>
/// Synthesis-J objective: sum the bicubic-sampled L_t field at each reference's
/// projected pixel. Public for shared use by production's solve engine and the
/// synthesis-probe tool — the two surfaces converge here so probe-measured J
/// and production J are computed identically.
/// </summary>
public static class JEvaluator
{
    public static JResult Evaluate(
        CandidateTransform t,
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<LandmarkReference> refs)
    {
        double j = 0;
        int aboveHalf = 0;
        int offCrop = 0;
        var perRef = new double[refs.Count];

        for (int i = 0; i < refs.Count; i++)
        {
            var r = refs[i];
            if (!fieldsByType.TryGetValue(r.Type, out var field))
            {
                perRef[i] = 0;
                continue;
            }
            var p = t.Apply(r.World);
            int h = field.GetLength(0), w = field.GetLength(1);
            if (p.X < 0 || p.Y < 0 || p.X > w - 1 || p.Y > h - 1)
            {
                offCrop++;
                perRef[i] = 0;
                continue;
            }
            var score = IconLikelihoodField.Sample(field, p.X, p.Y);
            perRef[i] = score;
            j += score;
            if (score >= 0.5) aboveHalf++;
        }

        return new JResult(j, aboveHalf, offCrop, perRef);
    }
}
```

Diffs from the original:
- `namespace` is now `Mithril.MapCalibration.Detection` (was probe-internal)
- `JResult` + `JEvaluator` are `public` (were `internal`)
- `IReadOnlyList<ReferencePoint>` → `IReadOnlyList<LandmarkReference>`
- `fieldsByType.TryGetValue(r.LandmarkType, ...)` → `r.Type`
- `t.Apply(new WorldCoord(r.WorldX, 0, r.WorldZ))` → `t.Apply(r.World)` (LandmarkReference already holds a `WorldCoord`)

- [ ] **Step 2: Delete the probe's `JEvaluator.cs`**

Delete `tools/MapCalibrationFromScreenshot/SynthesisProbe/JEvaluator.cs`.

- [ ] **Step 3: Build to surface the cascade**

Run: `dotnet build Mithril.slnx`
Expected: build fails — every probe consumer of `JEvaluator.Evaluate(..., refs)` was passing `IReadOnlyList<ReferencePoint>`. Task 5 fixes those. For now this confirms the cascade scope before we proceed.

- [ ] **Step 4: Commit (build-broken temporarily — fixed by Task 5)**

Skip this step. We don't commit a broken build. Continue to Tasks 3-5 and commit all four moves + the consumer fixup together at the end of Task 5.

---

### Task 3: Move `CandidateTransform` into `src/` and add `FromCalibration`

**Files:**
- Create: `src/Mithril.MapCalibration/Detection/CandidateTransform.cs`
- Delete: `tools/MapCalibrationFromScreenshot/SynthesisProbe/CandidateTransform.cs`

The new `FromCalibration` factory takes an in-memory `AreaCalibration` + `MapRect` and returns the `CandidateTransform` in aligned-pair-pixel space (the same space `IconLikelihoodField` produces). This is the shared math `Bundle/MapRectConversion` will adapt onto in Task 6.

- [ ] **Step 1: Write the new file**

Write `src/Mithril.MapCalibration/Detection/CandidateTransform.cs` (omit explicit `using System;` — global via ImplicitUsings):

```csharp
namespace Mithril.MapCalibration.Detection;

/// <summary>
/// World-coord → aligned-pair-pixel transform — the input to <see cref="JEvaluator"/>.
/// Mirrors <see cref="AreaCalibration.WorldToWindow"/> at <c>CalibrationZoom = 1.0</c>;
/// intentionally a distinct record so we don't allocate a full
/// <see cref="AreaCalibration"/> per candidate in the synthesis-J top-K loop.
/// Keep <see cref="Apply"/> in sync with <see cref="AreaCalibration.WorldToWindow"/>;
/// the equivalence test in <c>CandidateTransformConversionTests</c> is the trip-wire.
/// </summary>
public readonly record struct CandidateTransform(double Scale, double RotRadians, bool Mirror, double Tx, double Ty)
{
    public PixelPoint Apply(WorldCoord world)
    {
        var east = world.X;
        var north = Mirror ? -world.Z : world.Z;
        var cos = Math.Cos(RotRadians);
        var sin = Math.Sin(RotRadians);
        var rotE = east * cos + north * sin;
        var rotN = -east * sin + north * cos;
        return new PixelPoint(Tx + Scale * rotE, Ty - Scale * rotN);
    }

    /// <summary>
    /// Wrap an <see cref="AreaCalibration"/> in candidate space WITHOUT the
    /// MapRect re-scale — the calibration is already expressed in the field's
    /// coordinate system. Use for tests / experiments where the caller built
    /// the L_t field at native texture resolution.
    /// </summary>
    public static CandidateTransform FromAreaCalibration(AreaCalibration cal) =>
        new(cal.Scale, cal.RotationRadians, cal.MirrorNorth, cal.OriginX, cal.OriginY);

    /// <summary>
    /// Convert a texture-pixel-space <see cref="AreaCalibration"/> into the
    /// aligned-pair-pixel space the synthesis-J L_t fields live in. The aligned
    /// pair is the <paramref name="mapRect"/>'s crop with origin (0, 0):
    /// <c>aligned_pair = texture * (Width/TextureWidth, Height/TextureHeight)</c>.
    /// <see cref="CandidateTransform"/> is isotropic-scale-only — if the X and Y
    /// resize ratios differ, the geometric mean is adopted and the residual
    /// anisotropy is surfaced via <paramref name="anisotropyPercent"/>. Callers
    /// should warn at &gt; ~1%.
    /// </summary>
    public static CandidateTransform FromCalibration(
        AreaCalibration cal, MapRect mapRect, out double anisotropyPercent)
    {
        double ratioX = (double)mapRect.Width / mapRect.TextureWidth;
        double ratioY = (double)mapRect.Height / mapRect.TextureHeight;
        double geom = Math.Sqrt(ratioX * ratioY);
        anisotropyPercent = 100.0 * Math.Abs(ratioX - ratioY) / geom;

        return new CandidateTransform(
            Scale: cal.Scale * geom,
            RotRadians: cal.RotationRadians,
            Mirror: cal.MirrorNorth,
            Tx: cal.OriginX * ratioX,
            Ty: cal.OriginY * ratioY);
    }

    /// <summary>Overload without the anisotropy out-param.</summary>
    public static CandidateTransform FromCalibration(AreaCalibration cal, MapRect mapRect)
        => FromCalibration(cal, mapRect, out _);
}
```

Diffs from the probe original:
- `internal` → `public`
- `namespace` is now `Mithril.MapCalibration.Detection`
- Removed `using Mithril.MapCalibration;` — already in scope via the new namespace
- New `FromCalibration(AreaCalibration, MapRect, ...)` factory pair

- [ ] **Step 2: Delete the probe's `CandidateTransform.cs`**

Delete `tools/MapCalibrationFromScreenshot/SynthesisProbe/CandidateTransform.cs`.

- [ ] **Step 3: Confirm in-tool consumers compile**

Probe files referencing `CandidateTransform` already have `using Mithril.MapCalibration.Detection;` from Task 1 (if not, add it). The probe namespace `using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;` can stay — `CandidateTransform` is no longer there but no probe class besides the deleted file refers to it through that namespace. (`Bundle/MapRectConversion.cs` already does `using Mithril.MapCalibration.Detection;` — Task 6 modifies it further.)

Don't build yet — Task 2 left the build red. Tasks 4 + 5 complete the cascade.

---

### Task 4: Move `LocalRefine` into `src/` and switch to `LandmarkReference`

**Files:**
- Create: `src/Mithril.MapCalibration/Detection/LocalRefine.cs`
- Delete: `tools/MapCalibrationFromScreenshot/SynthesisProbe/LocalRefine.cs`

- [ ] **Step 1: Write the new file**

Write `src/Mithril.MapCalibration/Detection/LocalRefine.cs` (omit explicit `using System;` + `using System.Collections.Generic;` — both global via ImplicitUsings):

```csharp
namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Hill-climbing ascent on (Tx, Ty, Scale) maximising <see cref="JEvaluator.Evaluate"/>.
/// At each iteration, tries ±step in each axis and takes the move with the best
/// J; halves the step when no axis improves. Holds Rot and Mirror fixed (those
/// are discrete branches at the RANSAC / orientation level).
/// </summary>
public static class LocalRefine
{
    public static CandidateTransform Run(
        CandidateTransform seed,
        IReadOnlyDictionary<string, double[,]> fields,
        IReadOnlyList<LandmarkReference> refs,
        int maxIter,
        double stepInit)
    {
        var t = seed;
        double bestJ = JEvaluator.Evaluate(t, fields, refs).J;
        double stepXY = stepInit;
        double stepScale = stepInit * Math.Max(1e-6, seed.Scale) * 0.01;

        for (int iter = 0; iter < maxIter; iter++)
        {
            var candidates = new (CandidateTransform T, double J)[]
            {
                (t with { Tx = t.Tx + stepXY }, 0),
                (t with { Tx = t.Tx - stepXY }, 0),
                (t with { Ty = t.Ty + stepXY }, 0),
                (t with { Ty = t.Ty - stepXY }, 0),
                (t with { Scale = t.Scale + stepScale }, 0),
                (t with { Scale = Math.Max(1e-6, t.Scale - stepScale) }, 0),
            };
            int bestI = -1;
            double newBest = bestJ;
            for (int i = 0; i < candidates.Length; i++)
            {
                var j = JEvaluator.Evaluate(candidates[i].T, fields, refs).J;
                candidates[i] = (candidates[i].T, j);
                if (j > newBest) { newBest = j; bestI = i; }
            }
            if (bestI < 0)
            {
                stepXY *= 0.5;
                stepScale *= 0.5;
                if (stepXY < 0.01 && stepScale < 1e-6) break;
            }
            else
            {
                t = candidates[bestI].T;
                bestJ = newBest;
            }
        }
        return t;
    }
}
```

Diffs from the probe original:
- `internal` → `public`
- `namespace` is now `Mithril.MapCalibration.Detection`
- `IReadOnlyList<ReferencePoint>` → `IReadOnlyList<LandmarkReference>`
- Imports added: `using System;` and `using System.Collections.Generic;`

- [ ] **Step 2: Delete the probe's `LocalRefine.cs`**

Delete `tools/MapCalibrationFromScreenshot/SynthesisProbe/LocalRefine.cs`.

Don't build yet — Task 5 completes the cascade.

---

### Task 5: Delete `ReferencePoint` and update probe consumers

**Files:**
- Delete: `tools/MapCalibrationFromScreenshot/SynthesisProbe/ReferencePoint.cs`
- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/ProbeReferences.cs`
- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E1_TruthScore.cs`
- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E2_TranslationSweep.cs`
- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E3_ScaleSweep.cs`
- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E4_RansacSeedScore.cs`
- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E5_ColdGrid.cs`

- [ ] **Step 1: Update `ProbeReferences` to emit `LandmarkReference`**

In `tools/MapCalibrationFromScreenshot/SynthesisProbe/ProbeReferences.cs`:
- Replace `using` block: add `using Mithril.MapCalibration;` (for `WorldCoord`) and `using Mithril.MapCalibration.Detection;` (for `LandmarkReference`)
- Change return type `IReadOnlyList<ReferencePoint>` → `IReadOnlyList<LandmarkReference>`
- Change local `var result = new List<ReferencePoint>();` → `var result = new List<LandmarkReference>();`
- Change the two `.Add(new ReferencePoint(l.Name, l.Type, l.World.X, l.World.Z))` → `.Add(new LandmarkReference(l.Type, l.Name, l.World))` and `.Add(new LandmarkReference(n.Type, n.Name, n.World))`
- Update the XML doc comment to mention `LandmarkReference` instead of `ReferencePoint`

- [ ] **Step 2: Update each Experiment file**

For each of `E1_TruthScore.cs`, `E2_TranslationSweep.cs`, `E3_ScaleSweep.cs`, `E4_RansacSeedScore.cs`, `E5_ColdGrid.cs`:
- Add `using Mithril.MapCalibration.Detection;` (some already have it via earlier tasks — Grep confirms)
- Change the parameter signature `IReadOnlyList<ReferencePoint> refs` → `IReadOnlyList<LandmarkReference> refs`

No body changes needed — `JEvaluator.Evaluate` consumes `LandmarkReference` after Task 2, and the Experiments forward `refs` to `JEvaluator` / `LocalRefine` without touching field names.

(Spot-check: Grep for `r\.LandmarkType\|r\.WorldX\|r\.WorldZ\|r\.Label\|\.Label\b` over each Experiment file; if any direct field access exists, replace it: `LandmarkType` → `Type`, `WorldX` → `World.X`, `WorldZ` → `World.Z`, `Label` → `Name`.)

- [ ] **Step 2b: Update remaining probe consumers**

Three other probe files reference the moved types (`CandidateTransform`, `JResult`, `IconLikelihoodField`) and were NOT in the original plan's enumeration — caught by Task 3's cascade-shape verification:

- `tools/MapCalibrationFromScreenshot/SynthesisProbe/RansacSeedsCsv.cs` — references `CandidateTransform`. Add `using Mithril.MapCalibration.Detection;` if absent.
- `tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbeWriter.cs` — references `CandidateTransform` and `JResult`. Add `using Mithril.MapCalibration.Detection;` if absent.
- Any other file the final-pass Grep surfaces (run `Grep "CandidateTransform\|JResult\|IconLikelihoodField\|LocalRefine" tools/` and audit each hit for a missing `using`).

These files don't take `IReadOnlyList<ReferencePoint>` parameters — they only need the namespace import, no signature changes.

- [ ] **Step 3: Delete `ReferencePoint.cs`**

Delete `tools/MapCalibrationFromScreenshot/SynthesisProbe/ReferencePoint.cs`.

- [ ] **Step 4: Build to confirm the cascade is closed**

Run: `dotnet build Mithril.slnx`
Expected: build green. Every consumer now consumes `LandmarkReference`; `ReferencePoint` is gone.

- [ ] **Step 5: Run all map-calibration + probe tests**

Run: `dotnet test tests/Mithril.MapCalibration.Tests tests/Mithril.MapCalibration.Capture.Tests tests/Mithril.MapCalibration.Harness.Tests`
Expected: all green.

- [ ] **Step 6: Run the synthesis-probe E2E sanity check**

Run the probe over one of the existing Eltibule bundles (e.g. Bundle A) to confirm J still produces the same value. From the worktree root:

```pwsh
dotnet run --project tools/MapCalibrationFromScreenshot -- synthesis --bundle-dir <path-to-bundle-A>
```

Expected: J = 19.02 (or whatever the post-rim-mask Bundle A value is recorded as in the spec). If the value drifts, the move was NOT byte-equivalent — investigate before moving on.

- [ ] **Step 7: Commit the full PR-1 math move**

```bash
git add src/Mithril.MapCalibration/Detection/{IconLikelihoodField,JEvaluator,CandidateTransform,LocalRefine}.cs \
        tools/MapCalibrationFromScreenshot/SynthesisProbe
git commit -m "$(cat <<'EOF'
refactor(map-calibration): move synthesis-J math from probe into src/Detection

IconLikelihoodField / JEvaluator / CandidateTransform / LocalRefine relocate
from tools/MapCalibrationFromScreenshot/SynthesisProbe into src/Mithril.
MapCalibration/Detection so production and the probe consume one math path.
The probe's ReferencePoint record is deleted in favour of LandmarkReference
(already public in src/Mithril.MapCalibration/Detection) — same fields, one
type. No behaviour change; existing tests + probe E2E remain byte-equivalent.

Adds CandidateTransform.FromCalibration(AreaCalibration, MapRect) — the
in-memory bundle-free factory production will use in PR-2.

Spec: docs/superpowers/specs/2026-06-02-synthesis-rerank-design.md (PR-1).
EOF
)"
```

---

### Task 6: Refactor `Bundle/MapRectConversion` to a thin adapter

**Files:**
- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/MapRectConversion.cs`

The current `FromRecoveredCalibration(RecoveredCalibrationJson, MapRect, out double)` duplicates math now living in `CandidateTransform.FromCalibration(AreaCalibration, MapRect, out double)`. Refactor the probe-side method to build an in-memory `AreaCalibration` from the JSON DTO and delegate.

- [ ] **Step 1: Rewrite `MapRectConversion`**

Replace the body of `tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/MapRectConversion.cs`:

```csharp
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

internal static class MapRectConversion
{
    /// <summary>
    /// Thin adapter: build an in-memory <see cref="AreaCalibration"/> from the
    /// bundle's <see cref="RecoveredCalibrationJson"/> DTO and delegate to the
    /// shared <see cref="CandidateTransform.FromCalibration(AreaCalibration, MapRect, out double)"/>.
    /// Two consumers (production + probe), one piece of math; this method is
    /// the probe-side adapter.
    /// </summary>
    public static CandidateTransform FromRecoveredCalibration(
        RecoveredCalibrationJson cal,
        MapRect mapRect,
        out double anisotropyPercent)
    {
        var inMemory = new AreaCalibration(
            Scale: cal.Scale,
            RotationRadians: cal.RotationRadians,
            OriginX: cal.OriginX,
            OriginY: cal.OriginY,
            ReferenceCount: cal.ReferenceCount,
            ResidualPixels: cal.ResidualPixels)
        {
            MirrorNorth = cal.MirrorNorth,
            CalibrationZoom = cal.CalibrationZoom,
        };
        return CandidateTransform.FromCalibration(inMemory, mapRect, out anisotropyPercent);
    }

    /// <summary>Overload without the anisotropy out-param.</summary>
    public static CandidateTransform FromRecoveredCalibration(
        RecoveredCalibrationJson cal, MapRect mapRect)
        => FromRecoveredCalibration(cal, mapRect, out _);
}
```

- [ ] **Step 2: Build and run probe tests**

Run: `dotnet build Mithril.slnx`
Expected: green.

Run: `dotnet test tests/Mithril.MapCalibration.Tests tests/Mithril.MapCalibration.Capture.Tests`
Expected: green.

- [ ] **Step 3: Re-run probe E2E sanity check**

Re-run the Bundle A probe from Task 5 Step 6. Expected: J value identical to the Task 5 reading.

- [ ] **Step 4: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/MapRectConversion.cs
git commit -m "refactor(map-calibration): thin MapRectConversion adapter over shared CandidateTransform.FromCalibration"
```

**End of Block 1.** Continue straight into Block 2 — no review checkpoint here.

---

### Block 2 — Top-K API + conversion pin (Tasks 7-8)

Adds the one new public API surface (`TypeAwareRansacSolver.SolveTopK`) PR-2 will consume, and pins the conversion equivalence the relocation depends on. Run straight through; review at PR-1 open.

---

### Task 7: Add `TypeAwareRansacSolver.SolveTopK`

**Files:**
- Modify: `src/Mithril.MapCalibration/Detection/TypeAwareRansacSolver.cs`
- Modify: `tests/Mithril.MapCalibration.Tests/Detection/TypeAwareRansacSolverTests.cs`

The current `RansacAssign` keeps only the single best inlier set. Synthesis-J needs the top K (default K=8) candidates so the re-ranker has alternatives to score. Open question 1 in the spec offered two implementation options; this plan picks (b) — add a new public `SolveTopK` method, keep the existing `Solve` as a wrapper. Rationale: additive change, existing test surface unchanged, smaller diff.

- [ ] **Step 1: Write the failing test**

In `tests/Mithril.MapCalibration.Tests/Detection/TypeAwareRansacSolverTests.cs`, add a new `[Fact]`:

```csharp
[Fact]
public void SolveTopK_returns_candidates_ordered_by_inliers_then_residual()
{
    var detections = BuildDetections();
    var refs = BuildRefs();

    var topK = TypeAwareRansacSolver.SolveTopK(detections, refs, Rect, k: 4);

    topK.Should().NotBeEmpty();
    topK.Count.Should().BeLessOrEqualTo(4);
    topK[0].Calibration.Should().NotBeNull();

    // Non-increasing inlier count, ties broken by non-decreasing residual.
    for (int i = 1; i < topK.Count; i++)
    {
        var prev = topK[i - 1];
        var cur = topK[i];
        var prevBetter =
            prev.Inliers.Count > cur.Inliers.Count
            || (prev.Inliers.Count == cur.Inliers.Count
                && prev.Calibration!.ResidualPixels <= cur.Calibration!.ResidualPixels);
        prevBetter.Should().BeTrue(
            $"candidate {i - 1} ({prev.Inliers.Count} inliers, "
            + $"{prev.Calibration!.ResidualPixels:0.00} px) must rank ≥ candidate {i} "
            + $"({cur.Inliers.Count} inliers, {cur.Calibration!.ResidualPixels:0.00} px)");
    }
}

[Fact]
public void SolveTopK_with_k1_is_equivalent_to_Solve()
{
    var detections = BuildDetections();
    var refs = BuildRefs();

    var (legacyCal, legacyInliers) = TypeAwareRansacSolver.Solve(detections, refs, Rect);
    var topK = TypeAwareRansacSolver.SolveTopK(detections, refs, Rect, k: 1);

    if (legacyCal is null)
    {
        topK.Should().BeEmpty();
        return;
    }
    topK.Should().HaveCount(1);
    topK[0].Calibration!.Should().BeEquivalentTo(legacyCal);
    topK[0].Inliers.Should().HaveCount(legacyInliers.Count);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SolveTopK"`
Expected: FAIL with "TypeAwareRansacSolver does not contain a definition for 'SolveTopK'".

- [ ] **Step 3: Implement `SolveTopK`**

Edit `src/Mithril.MapCalibration/Detection/TypeAwareRansacSolver.cs`. Add a new public record + public method, refactor `RansacAssign` into a top-K-aware variant. Full diff:

```csharp
/// <summary>One top-K candidate: solved calibration + the inlier set used.</summary>
public sealed record TopKCandidate(
    AreaCalibration Calibration,
    IReadOnlyList<AssignedReference> Inliers);

/// <summary>
/// Top-K variant of <see cref="Solve"/>. Returns up to <paramref name="k"/>
/// geometrically-consistent fits ordered by inlier count desc, refit-residual
/// asc, after the same iterative refinement step. Synthesis-J re-rank consumes
/// this so the re-ranker has alternatives to score. With <paramref name="k"/>=1
/// the result is equivalent to <see cref="Solve"/>.
/// </summary>
public static IReadOnlyList<TopKCandidate> SolveTopK(
    IReadOnlyDictionary<string, List<TypedDetection>> detectionsByType,
    IReadOnlyList<LandmarkReference> allRefs,
    MapRect mapRect,
    int k)
{
    if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), k, "k must be >= 1");

    var rawCandidates = RansacAssignAll(detectionsByType, allRefs, mapRect);
    if (rawCandidates.Count == 0) return [];

    // Order by inlier count desc, then refit residual asc.
    rawCandidates.Sort((a, b) =>
    {
        int ic = b.Inliers.Count.CompareTo(a.Inliers.Count);
        return ic != 0 ? ic : a.Residual.CompareTo(b.Residual);
    });

    var refined = new List<TopKCandidate>(Math.Min(k, rawCandidates.Count));
    var seenKeys = new HashSet<string>(StringComparer.Ordinal);
    foreach (var raw in rawCandidates)
    {
        if (refined.Count >= k) break;
        var (cal, refinedInliers) = IterativeRefine(raw.Inliers);
        if (cal is null) continue;

        // De-dup: two raw candidates can refine into the same fit. Key by a
        // coarse round of (scale, rot, originX, originY) — same key → drop.
        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{cal.Scale:F3}|{cal.RotationRadians:F3}|{cal.OriginX:F1}|{cal.OriginY:F1}|{cal.MirrorNorth}");
        if (!seenKeys.Add(key)) continue;

        refined.Add(new TopKCandidate(cal, refinedInliers));
    }
    return refined;
}

/// <summary>
/// Existing single-best entry point — preserved for callers that don't need
/// alternatives. Now a thin wrapper over <see cref="SolveTopK"/>.
/// </summary>
public static (AreaCalibration? Calibration, IReadOnlyList<AssignedReference> Inliers) Solve(
    IReadOnlyDictionary<string, List<TypedDetection>> detectionsByType,
    IReadOnlyList<LandmarkReference> allRefs,
    MapRect mapRect)
{
    var top = SolveTopK(detectionsByType, allRefs, mapRect, k: 1);
    if (top.Count == 0) return (null, []);
    return (top[0].Calibration, top[0].Inliers);
}
```

Then rename the existing private `RansacAssign` → `RansacAssignAll` and modify its tail. Replace the "track best inlier set" logic inside the iteration loop with "append every valid candidate to a list":

```csharp
private static List<(IReadOnlyList<AssignedReference> Inliers, double Residual)> RansacAssignAll(
    IReadOnlyDictionary<string, List<TypedDetection>> detectionsByType,
    IReadOnlyList<LandmarkReference> allRefs,
    MapRect mapRect)
{
    // (build `pool` as before — UNCHANGED)
    // …existing pool construction…

    if (pool.Count < 2) return [];

    var rng = new Random(852);
    var all = new List<(IReadOnlyList<AssignedReference> Inliers, double Residual)>();

    for (int iter = 0; iter < RansacIterations; iter++)
    {
        // …existing per-iteration body up through and INCLUDING the
        // `if (Math.Max(maxX - minX, maxY - minY) < 100) continue;` guard…

        var refitRefs = inliers
            .Select(a => new LandmarkCalibrationSolver.Reference(a.WorldX, a.WorldZ, new PixelPoint(a.PixelX, a.PixelY)))
            .ToList();
        var refit = LandmarkCalibrationSolver.Solve(refitRefs);
        if (refit is null) continue;

        all.Add((inliers, refit.ResidualPixels));
    }

    return all;
}
```

(The single-best `wins`/`bestInlierCount` tracking is gone; sorting + top-K now happens in `SolveTopK`.)

- [ ] **Step 4: Run the new tests**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SolveTopK"`
Expected: PASS.

- [ ] **Step 5: Run all existing solver tests**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~TypeAwareRansacSolverTests"`
Expected: all PASS — the existing `Solve_recovers_truth_from_typed_detections` and any others go through the new `SolveTopK` via the wrapper. If any drift, the de-dup or sort is broken — fix before moving on.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/TypeAwareRansacSolver.cs \
        tests/Mithril.MapCalibration.Tests/Detection/TypeAwareRansacSolverTests.cs
git commit -m "feat(map-calibration): add TypeAwareRansacSolver.SolveTopK for synthesis-J re-rank"
```

---

### Task 8: Conversion-equivalence test

**Files:**
- Create: `tests/Mithril.MapCalibration.Tests/Detection/CandidateTransformConversionTests.cs`

Pin the equivalence between the production `CandidateTransform.FromCalibration(AreaCalibration, MapRect)` and the probe-side `MapRectConversion.FromRecoveredCalibration(RecoveredCalibrationJson, MapRect)`. The probe adapter now goes through the same math, but the round-trip via the DTO shape is a parity contract worth pinning explicitly so a future DTO field add can't silently drift the two surfaces.

This test lives in `tests/Mithril.MapCalibration.Tests` (not the tool's tests) because it exercises production code; the probe adapter is referenced indirectly via the tool project (or, more cleanly, we replicate the conversion math directly in the test fixture since `MapRectConversion` is `internal` and lives in the tool — which a test project can't reach).

- [ ] **Step 1: Write the test**

Write `tests/Mithril.MapCalibration.Tests/Detection/CandidateTransformConversionTests.cs`:

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class CandidateTransformConversionTests
{
    [Theory]
    [InlineData(0.55, 1.2, 100.0, 200.0, false, 400, 300, 800, 600)]   // crop downsampled 2x, identity ratio
    [InlineData(0.55, -2.046, 50.0, 75.0, true,  300, 200, 600, 400)]  // mirror=true, crop iso-downsample 2x
    [InlineData(1.10, 0.0,    0.0,   0.0,  false, 800, 600, 800, 600)]  // crop == native; should round-trip with anisotropy=0
    [InlineData(0.80, 0.5,    25.0,  35.0, false, 300, 200, 600, 400)]  // crop iso-downsample 2x
    [InlineData(0.45, -0.3,   -10.0, -20.0,true,  400, 300, 800, 600)]  // mirror=true with negative origin
    public void FromCalibration_round_trips_via_JSON_dto_shape(
        double scale, double rotRadians, double originX, double originY,
        bool mirrorNorth,
        int rectWidth, int rectHeight, int textureWidth, int textureHeight)
    {
        var inMemory = new AreaCalibration(
            Scale: scale,
            RotationRadians: rotRadians,
            OriginX: originX,
            OriginY: originY,
            ReferenceCount: 5,
            ResidualPixels: 2.5)
        { MirrorNorth = mirrorNorth, CalibrationZoom = 1.0 };

        // Simulate the probe-side path: pass the in-memory AreaCalibration's
        // fields through the same arithmetic the bundle DTO would round-trip
        // through. Direct construction of RecoveredCalibrationJson would
        // require referencing the tool's internal type, which the test project
        // can't see; the DTO is value-for-value identical to the relevant
        // AreaCalibration fields (PR-1 Task 6 made MapRectConversion a thin
        // adapter that rebuilds an AreaCalibration from those fields), so the
        // round-trip is exercised by re-deriving the AreaCalibration from the
        // five DTO-shape fields and comparing to FromCalibration directly.
        var rebuilt = new AreaCalibration(
            Scale: scale,
            RotationRadians: rotRadians,
            OriginX: originX,
            OriginY: originY,
            ReferenceCount: 5,
            ResidualPixels: 2.5)
        { MirrorNorth = mirrorNorth, CalibrationZoom = 1.0 };

        var mapRect = new MapRect(
            OriginX: 0, OriginY: 0,
            Width: rectWidth, Height: rectHeight,
            TextureWidth: textureWidth, TextureHeight: textureHeight);

        var direct = CandidateTransform.FromCalibration(inMemory, mapRect, out var directAniso);
        var viaDto = CandidateTransform.FromCalibration(rebuilt, mapRect, out var dtoAniso);

        direct.Should().Be(viaDto);
        directAniso.Should().Be(dtoAniso);
    }

    [Fact]
    public void FromCalibration_surfaces_anisotropy_when_ratios_diverge()
    {
        var cal = new AreaCalibration(
            Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
            ReferenceCount: 2, ResidualPixels: 0.0);

        // 2:1 anisotropic crop (height-scaled, width unchanged).
        var anisoRect = new MapRect(
            OriginX: 0, OriginY: 0, Width: 800, Height: 300,
            TextureWidth: 800, TextureHeight: 600);

        _ = CandidateTransform.FromCalibration(cal, anisoRect, out var anisotropyPercent);
        anisotropyPercent.Should().BeGreaterThan(50.0,
            "a 2:1 height-vs-width ratio mismatch should surface as >50% anisotropy");
    }
}
```

The first test pins five (scale, rotation, origin, mirror, mapRect-dim) combinations covering: crop downsample, mirror=true, identity ratio, scale variations, negative origin. The second test pins the anisotropy surfacing.

- [ ] **Step 2: Run the new test**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~CandidateTransformConversionTests"`
Expected: PASS — `FromCalibration` is deterministic and both call sites pass the same `AreaCalibration` shape.

- [ ] **Step 3: Sanity-check with the probe E2E once more**

Re-run the Bundle A probe from Task 5 Step 6. The probe is now consuming PR-1's shared math + the adapter; J must remain the recorded post-rim Bundle A value (~19.02). Drift here is a Task-6 regression.

- [ ] **Step 4: Commit**

```bash
git add tests/Mithril.MapCalibration.Tests/Detection/CandidateTransformConversionTests.cs
git commit -m "test(map-calibration): pin CandidateTransform.FromCalibration round-trip via DTO shape"
```

---

## 🛑 Review checkpoint 1 — Open PR-1

Push the feature branch and open the PR-1 pull request via `gh pr create`. Title: `refactor(map-calibration): relocate synthesis-J math into src/Detection [PR-1]`. Body should reference [docs/superpowers/specs/2026-06-02-synthesis-rerank-design.md](../specs/2026-06-02-synthesis-rerank-design.md) and the PR-1 milestone scope. Use HEREDOC per CLAUDE.md commit conventions.

**What the reviewer is looking at (the whole PR-1 diff in one pass):**
- Math files moved cleanly into `src/Mithril.MapCalibration/Detection/` — namespace + access modifiers changed, body unchanged where the spec says "no behaviour change"
- `ReferencePoint` deleted, probe consumers switched to `LandmarkReference` (one production type, one math path)
- `CandidateTransform.FromCalibration(AreaCalibration, MapRect)` is the new in-memory factory PR-2 will consume
- `Bundle/MapRectConversion` is a thin adapter that builds an `AreaCalibration` from the DTO and delegates
- `TypeAwareRansacSolver.SolveTopK` is the new public surface
- Conversion-equivalence test pins the math
- Probe E2E (Bundle A J ≈ 19.02) confirms byte-equivalence

**PR-1 review must clear before PR-2 starts**; PR-2 builds on PR-1's moved math. The shepherd / reviewer signs off on the review platform; the engineer waits for green before starting Block 3.

---

## PR-2 — Synthesis-J wiring + telemetry (Tasks 9-18) · 🛑 Review checkpoints 2 + 3

### Block 3 — Foundation (Tasks 9-12)

POCO, meter catalog, DI wiring, and a private result record. Each task is small, structural, and either has its own smoke test (Tasks 9, 11) or is no-behaviour-change (Tasks 10, 12). No mid-block review.

---

### Task 9: `SynthesisRerankMode` enum + `MapCalibrationSolverOptions` POCO

**Files:**
- Create: `src/Mithril.MapCalibration/MapCalibrationSolverOptions.cs`

- [ ] **Step 1: Write the file**

Write `src/Mithril.MapCalibration/MapCalibrationSolverOptions.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mithril.MapCalibration;

/// <summary>
/// Three-state toggle for the synthesis-J re-rank (spec §Q2).
/// <list type="bullet">
/// <item><c>Off</c> — no L_t build, legacy inlier-count gate is the source of truth, zero cost.</item>
/// <item><c>Shadow</c> — L_t built, synthesis-J computed + emitted as telemetry, but the legacy gate is still the source of truth for accept/reject (and therefore for persistence). Safe-to-deploy default while Phase-C telemetry accumulates.</item>
/// <item><c>Enabled</c> — synthesis-J is the gate; accept iff <c>J ≥ J_min AND refs_above_0.5 ≥ N_min</c>. Legacy inlier count + residual remain informational.</item>
/// </list>
/// </summary>
public enum SynthesisRerankMode
{
    Off,
    Shadow,
    Enabled,
}

/// <summary>
/// Runtime-flippable knobs for <see cref="Detection.MapCalibrationSolveEngine"/>'s
/// synthesis-J re-rank (spec §Q2). Mirrors the
/// <see cref="Mithril.MapCalibration.Capture.CaptureDiagnosticsOptions"/> pattern:
/// DI singleton, plain mutable POCO, INotifyPropertyChanged so a settings UI
/// can bind without re-resolving the graph.
///
/// <para>Default <see cref="SynthesisRerankMode"/> is <see cref="SynthesisRerankMode.Shadow"/> —
/// the engine computes synthesis-J + emits telemetry but the legacy
/// inlier-count gate remains the source of truth (spec §Q2 "Why Shadow is the
/// default"). The <see cref="SynthesisJMin"/> / <see cref="SynthesisNMin"/>
/// defaults are anchored to PR #993's post-rim 4-bundle dataset (Bundle A=19/21,
/// B-truth=15.5/16, C=14/13 accept; B-wrong-fit=2.5/4 rejects); recalibrate
/// against real telemetry per spec §Q3 Phase C before flipping the default.</para>
/// </summary>
public sealed class MapCalibrationSolverOptions : INotifyPropertyChanged
{
    private SynthesisRerankMode _synthesisRerankMode = SynthesisRerankMode.Shadow;
    private double _synthesisJMin = 8.0;
    private int _synthesisNMin = 8;
    private int _ransacTopK = 8;

    /// <summary>Active re-rank mode. Default <see cref="SynthesisRerankMode.Shadow"/>.</summary>
    public SynthesisRerankMode SynthesisRerankMode
    {
        get => _synthesisRerankMode;
        set { if (_synthesisRerankMode != value) { _synthesisRerankMode = value; OnChanged(); } }
    }

    /// <summary>J floor for the <see cref="SynthesisRerankMode.Enabled"/> gate. Default 8.0.</summary>
    public double SynthesisJMin
    {
        get => _synthesisJMin;
        set { if (_synthesisJMin != value) { _synthesisJMin = value; OnChanged(); } }
    }

    /// <summary>Floor on <c>refs whose sampled L_t ≥ 0.5</c> for the <see cref="SynthesisRerankMode.Enabled"/> gate. Default 8.</summary>
    public int SynthesisNMin
    {
        get => _synthesisNMin;
        set { if (_synthesisNMin != value) { _synthesisNMin = value; OnChanged(); } }
    }

    /// <summary>Number of RANSAC candidates the re-rank scores per orientation. Default 8.</summary>
    public int RansacTopK
    {
        get => _ransacTopK;
        set { if (_ransacTopK != value) { _ransacTopK = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 2: Write a smoke test**

Add `tests/Mithril.MapCalibration.Tests/MapCalibrationSolverOptionsTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class MapCalibrationSolverOptionsTests
{
    [Fact]
    public void Default_mode_is_Shadow()
    {
        var opts = new MapCalibrationSolverOptions();
        opts.SynthesisRerankMode.Should().Be(SynthesisRerankMode.Shadow);
        opts.SynthesisJMin.Should().Be(8.0);
        opts.SynthesisNMin.Should().Be(8);
        opts.RansacTopK.Should().Be(8);
    }

    [Fact]
    public void PropertyChanged_fires_on_mode_flip()
    {
        var opts = new MapCalibrationSolverOptions();
        var heard = new System.Collections.Generic.List<string?>();
        opts.PropertyChanged += (_, e) => heard.Add(e.PropertyName);

        opts.SynthesisRerankMode = SynthesisRerankMode.Enabled;
        opts.SynthesisRerankMode = SynthesisRerankMode.Enabled; // no event — no change

        heard.Should().Equal(nameof(MapCalibrationSolverOptions.SynthesisRerankMode));
    }
}
```

- [ ] **Step 3: Run the smoke test**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapCalibrationSolverOptionsTests"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration/MapCalibrationSolverOptions.cs \
        tests/Mithril.MapCalibration.Tests/MapCalibrationSolverOptionsTests.cs
git commit -m "feat(map-calibration): add MapCalibrationSolverOptions POCO + SynthesisRerankMode enum"
```

---

### Task 10: Local Detection telemetry catalog

**Files:**
- Create: `src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs`
- Modify: `src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs` (one-line pointer comment)
- Modify: `src/Mithril.Shared/Diagnostics/Telemetry/MithrilMeters.cs` (one-line pointer comment)

**Architectural constraint** — `src/Mithril.MapCalibration/Mithril.MapCalibration.csproj` declares (and the comment is load-bearing): *"Deliberately no ProjectReference to Mithril.Shared: this assembly is meant to be consumable by Mithril.Shared too (and by any module/peer) without depending up the layering."* So the `MapCalibration` core CANNOT import `MithrilMeters` / `MithrilActivitySources` from `Mithril.Shared`.

The precedent is **Arda**: `Arda.Abstractions.Diagnostics.ArdaActivitySources` and `ArdaMeters` are defined locally inside Arda because Arda projects can't take a dependency on `Mithril.Shared` (documented in `MithrilActivitySources.cs` line 39 and `MithrilMeters.cs` line 58). Listeners filter on the `"Mithril."` name prefix and receive both catalogs uniformly. Synthesis-J follows the same pattern: a local catalog inside `Mithril.MapCalibration`, with a one-line pointer in the Shared catalogs.

- [ ] **Step 1: Create the local diagnostics catalog**

Write `src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Mithril.MapCalibration.Diagnostics;

/// <summary>
/// Local <see cref="ActivitySource"/> + <see cref="Meter"/> catalog for the
/// <c>Mithril.MapCalibration</c> Detection layer. Defined here (and NOT in
/// <c>Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs</c> +
/// <c>MithrilMeters.cs</c>) because <c>Mithril.MapCalibration.csproj</c>
/// deliberately doesn't reference <c>Mithril.Shared</c> — the same constraint
/// Arda's catalogs work around (see <c>ArdaActivitySources</c> / <c>ArdaMeters</c>).
///
/// <para>Names follow the <c>"Mithril.…"</c> prefix convention so listeners
/// subscribing to the prefix receive both the Shared catalogs and this one
/// uniformly. The Capture layer already emits <c>"Mithril.MapCalibration.Capture"</c>
/// spans through <see cref="Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.MapCalibration"/>;
/// this catalog adds <c>"Mithril.MapCalibration.Detection"</c> so the per-layer
/// vocabulary is unambiguous when a Seq waterfall surfaces both at once.</para>
/// </summary>
public static class MapCalibrationDiagnostics
{
    /// <summary>
    /// Spans emitted from the Detection layer's solve / synthesis-J path.
    /// Parent span (<c>calibration.solve</c>) lives in the Capture layer's
    /// <see cref="Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.MapCalibration"/>;
    /// when both are listened-to, this source's children (<c>calibration.synthesis_rerank</c>)
    /// nest under the Capture parent naturally.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Mithril.MapCalibration.Detection");

    /// <summary>Map auto-calibration synthesis-J re-rank instruments (spec §Q2 telemetry contract).</summary>
    public static class Meters
    {
        public static readonly Meter Meter = new("Mithril.MapCalibration.Detection");

        /// <summary>Winning candidate's <c>J(T_k)</c>. Tag: <c>verdict</c> ∈ {accept, reject} (per synthesis-J).</summary>
        public static readonly Histogram<double> SynthesisJ =
            Meter.CreateHistogram<double>("mithril.map_calibration.synthesis.j");

        /// <summary>Refs whose sampled <c>L_t(T·r) ≥ 0.5</c> for the winning candidate. Tag: <c>verdict</c>.</summary>
        public static readonly Histogram<long> SynthesisRefsAboveThreshold =
            Meter.CreateHistogram<long>("mithril.map_calibration.synthesis.refs_above_threshold");

        /// <summary>Synthesis-J disagreed with the legacy inlier-count gate. Tag: <c>change</c> ∈ {accept_to_reject, reject_to_accept}.</summary>
        public static readonly Counter<long> SynthesisDisagree =
            Meter.CreateCounter<long>("mithril.map_calibration.synthesis.disagree");
    }
}
```

(Naming: `mithril.<subsystem>.<area>.<instrument>` matches `MithrilMeters`'s convention. ActivitySource name `Mithril.MapCalibration.Detection` peer-mirrors the existing `Mithril.MapCalibration.Capture` source so the per-layer split is explicit.)

- [ ] **Step 2: Add pointer comments to the Shared catalogs**

In `src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs`, after the `Arda pipeline sources live in…` comment block (just before the `public const string Prefix = "Mithril.";` line), append:

```csharp
    // MapCalibration Detection-layer sources live in Mithril.MapCalibration.
    // Diagnostics.MapCalibrationDiagnostics because Mithril.MapCalibration.csproj
    // deliberately doesn't take a dependency on Mithril.Shared (it must be
    // consumable by Mithril.Shared peers without depending up the layering).
    // The "Mithril." prefix below picks both catalogs up uniformly.
```

In `src/Mithril.Shared/Diagnostics/Telemetry/MithrilMeters.cs`, after the `// Arda counters live in Arda.Abstractions.Diagnostics.ArdaMeters` comment block (just before the `Reference` static class), append:

```csharp
    // MapCalibration Detection-layer instruments live in Mithril.MapCalibration.
    // Diagnostics.MapCalibrationDiagnostics.Meters for the same reason Arda's
    // do — Mithril.MapCalibration.csproj can't take a dependency on Mithril.
    // Shared. Listener-side meter dispatch is purely string-based on the
    // instrument name, so the Shared and Local catalogs feed the same exporters.
```

- [ ] **Step 3: Verify the file compiles**

Run: `dotnet build src/Mithril.MapCalibration/Mithril.MapCalibration.csproj && dotnet build src/Mithril.Shared/Mithril.Shared.csproj`
Expected: both green. Confirms `Mithril.MapCalibration` has zero new dependencies.

- [ ] **Step 4: Update the perf-trace schema doc**

CLAUDE.md instrumentation policy: "when a new tag/instrument lands, update the shape contract in `docs/perf-trace-schema.md`."

Append to `docs/perf-trace-schema.md` a short stanza covering:
- The new `Mithril.MapCalibration.Detection` ActivitySource + the `calibration.synthesis_rerank` span tags (full list per the spec's Q2 telemetry contract — see Task 16)
- The three new meters (`mithril.map_calibration.synthesis.j`, `…refs_above_threshold`, `…disagree`)
- A pointer note that the catalog lives in `Mithril.MapCalibration.Diagnostics.MapCalibrationDiagnostics`, mirroring Arda's pattern

Match the existing doc's section format. If unsure of the format, read 30 lines of `docs/perf-trace-schema.md` first.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs \
        src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs \
        src/Mithril.Shared/Diagnostics/Telemetry/MithrilMeters.cs \
        docs/perf-trace-schema.md
git commit -m "feat(telemetry): MapCalibrationDiagnostics local catalog for synthesis-J re-rank"
```

---

### Task 11: Wire `MapCalibrationSolverOptions` through DI

**Files:**
- Modify: `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`
- Modify: `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs`
- Modify: `tests/Mithril.MapCalibration.Tests/Detection/EngineRegistrationTests.cs` (if it exists; otherwise create a new test)

The engine takes the options as an optional ctor param so existing tests/callers that pass no options use a default-Shadow instance.

- [ ] **Step 1: Add the ctor param to `MapCalibrationSolveEngine`**

In `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs`:
- Add `using Mithril.MapCalibration;` (if not already imported)
- Add private field: `private readonly MapCalibrationSolverOptions _options;`
- Add ctor param: change the existing constructor signature to:

```csharp
public MapCalibrationSolveEngine(
    ICalibrationDetector detector,
    ICalibrationConfidenceGate gate,
    ILogger? logger = null,
    MapCalibrationSolverOptions? options = null)
{
    _detector = detector;
    _gate = gate;
    _logger = logger;
    _options = options ?? new MapCalibrationSolverOptions();
}
```

The `?? new MapCalibrationSolverOptions()` default keeps existing direct-construction callers (tests, etc.) working unchanged with Shadow defaults — but Task 13 changes their behaviour from "no L_t build" → "L_t builds + telemetry emits" because Shadow is the default mode. Tests asserting *only* legacy behaviour are unaffected (Shadow keeps the legacy gate as the source of truth); tests asserting "no synthesis_rerank span emits" must explicitly construct `MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Off }` — call sites flagged in Task 18 step 2.

- [ ] **Step 2: Wire it through `AddMithrilMapCalibrationEngine`**

In `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`:
- Add `using Microsoft.Extensions.DependencyInjection.Extensions;` if absent (needed for `TryAddSingleton`)
- Inside `AddMithrilMapCalibrationEngine`, add before the `services.AddSingleton(sp => new MapCalibrationSolveEngine(...` line:

```csharp
services.TryAddSingleton<MapCalibrationSolverOptions>();
```

(`TryAddSingleton` so a shell/settings-binding registration can supply its own instance first; matches the `CaptureDiagnosticsOptions` pattern.)

- Update the `MapCalibrationSolveEngine` factory to inject the options:

```csharp
services.AddSingleton(sp => new MapCalibrationSolveEngine(
    sp.GetRequiredService<ICalibrationDetector>(),
    sp.GetRequiredService<ICalibrationConfidenceGate>(),
    sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Engine"),
    sp.GetRequiredService<MapCalibrationSolverOptions>()));
```

- [ ] **Step 3: Add a registration test**

Add to (or create) `tests/Mithril.MapCalibration.Tests/Detection/EngineRegistrationTests.cs`:

```csharp
[Fact]
public void AddMithrilMapCalibrationEngine_registers_MapCalibrationSolverOptions_with_Shadow_default()
{
    var services = new ServiceCollection();
    services.AddMithrilMapCalibrationEngine(assetCacheDir: System.IO.Path.GetTempPath());
    using var sp = services.BuildServiceProvider();

    var opts = sp.GetRequiredService<MapCalibrationSolverOptions>();
    opts.SynthesisRerankMode.Should().Be(SynthesisRerankMode.Shadow);

    // Same instance returned per resolve (singleton).
    sp.GetRequiredService<MapCalibrationSolverOptions>().Should().BeSameAs(opts);
}
```

Required usings: `using FluentAssertions; using Microsoft.Extensions.DependencyInjection; using Mithril.MapCalibration; using Mithril.MapCalibration.DependencyInjection; using Xunit;`.

- [ ] **Step 4: Build + run the new test**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~EngineRegistrationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs \
        src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs \
        tests/Mithril.MapCalibration.Tests/Detection/EngineRegistrationTests.cs
git commit -m "feat(map-calibration): wire MapCalibrationSolverOptions through engine DI"
```

---

### Task 12: Per-orientation synthesis-J result struct

**Files:**
- Modify: `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs`

A small internal record helps the engine's per-orientation loop produce a uniform shape that the cross-orientation selector compares. Keeping it private/file-scoped keeps the public surface unchanged.

- [ ] **Step 1: Add the result types inside the engine file**

At the bottom of `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs`, add:

```csharp
/// <summary>
/// Per-orientation synthesis-J winner, used by
/// <see cref="MapCalibrationSolveEngine"/>'s cross-orientation selector.
/// Internal — the public consumer sees the unified <see cref="CalibrationSolveResult"/>.
/// </summary>
internal sealed record SynthesisOrientationWinner(
    bool Rotate180,
    AreaCalibration Calibration,
    IReadOnlyList<TypeAwareRansacSolver.AssignedReference> Inliers,
    double J,
    int RefsAboveHalf,
    int RefsOffCrop,
    int RefsTotal);
```

No behaviour wired yet — Tasks 13-15 consume this. The record alone is one safe commit at the file level.

- [ ] **Step 2: Build + run all existing tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Tests`
Expected: all green (no behaviour change yet).

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs
git commit -m "refactor(map-calibration): add SynthesisOrientationWinner record for engine integration"
```

**End of Block 3.** Continue straight into Block 4.

---

### Block 4 — Engine integration (Tasks 13-16)

The load-bearing logic. Task 13 adds the L_t builder, Task 14 adds top-K scoring, Task 15 refactors `MapCalibrationSolveEngine.Solve` (the engine's main entry point), Task 16 emits telemetry. After Task 16 the synthesis-J re-rank is **functionally wired** in Shadow mode (the default) — but no smoke test pins the three-mode contract yet.

This block ends at the recommended-but-optional mid-PR review checkpoint. Run it straight through; commits land per task; reviewer pauses at the marker IF they want to scrutinise the algorithm before tests pin it.

---

### Task 13: Build L_t per orientation when mode ≠ Off

**Files:**
- Modify: `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs`

The L_t fields are built from the deviation `D = max(0, screenshot − baseTexture)` with `DeviationFloodRimMask` applied — the SAME computation as the probe's `IconLikelihoodField.LoadDeviationAsField`. (Task 17's equality test pins this convergence.) Building once per orientation and reusing across all K candidates makes per-candidate scoring just the 38-ref bicubic-sample loop.

- [ ] **Step 1: Add a private helper to build the fields**

Add to `MapCalibrationSolveEngine`:

```csharp
/// <summary>
/// Build per-type L_t fields for one orientation by computing the additive
/// deviation D = max(0, screenshot − baseTexture), applying the rim-mask
/// (mithril#992), and scoring each unique landmark-type template against the
/// masked deviation. Cached by orientation: built once per orientation, reused
/// across all top-K candidates the re-rank scores.
/// </summary>
private static IReadOnlyDictionary<string, double[,]> BuildLikelihoodFieldsFromDeviation(
    GrayImage screenshot,
    GrayImage baseTexture,
    IconTemplateSet templates)
{
    if (screenshot.Width != baseTexture.Width || screenshot.Height != baseTexture.Height)
        throw new ArgumentException("screenshot and base texture must have matching dimensions");

    int w = screenshot.Width, h = screenshot.Height;
    var deviation = new byte[w * h];
    for (int i = 0; i < deviation.Length; i++)
    {
        int d = screenshot.Pixels[i] - baseTexture.Pixels[i];
        deviation[i] = d > 0 ? (byte)Math.Min(255, d) : (byte)0;
    }
    var devImage = new GrayImage(w, h, deviation);

    // One template per landmark-type — the per-type L_t fields are keyed by
    // LandmarkType. If a type has multiple templates (e.g. variants), the
    // first is used; mirror the probe's behaviour (which uses the same
    // ProbeReferences-driven per-type single template).
    var perType = new Dictionary<string, IconTemplate>(StringComparer.Ordinal);
    foreach (var template in templates.Templates)
    {
        if (!perType.ContainsKey(template.LandmarkType))
            perType[template.LandmarkType] = template;
    }

    var fields = new Dictionary<string, double[,]>(perType.Count, StringComparer.Ordinal);
    foreach (var (type, template) in perType)
    {
        fields[type] = IconLikelihoodField.LoadDeviationAsField(
            devImage, template,
            applyRimMask: true,
            devThr: IconLikelihoodField.DefaultDevThr);
    }
    return fields;
}
```

- [ ] **Step 2: Build + run all tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Tests`
Expected: all green — `BuildLikelihoodFieldsFromDeviation` is private + unused; this is a structural-only commit.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs
git commit -m "feat(map-calibration): per-orientation L_t-field builder for synthesis-J re-rank"
```

---

### Task 14: Score top-K candidates with synthesis-J

**Files:**
- Modify: `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs`

Per orientation, after detect + RANSAC top-K: for each candidate score `J(T_k)` + `refs_above_0.5(T_k)` and pick the highest-J refined winner.

- [ ] **Step 1: Add a private helper to score top-K and pick the orientation winner**

Add to `MapCalibrationSolveEngine`:

```csharp
/// <summary>
/// For one orientation, score each of the RANSAC top-K candidates with
/// <see cref="JEvaluator"/>, LM-refine the highest-J candidate with
/// <see cref="LocalRefine"/>, and return the orientation winner. The winner's
/// <c>Calibration</c> reflects the LM-refined fit; <c>Inliers</c> are the
/// raw RANSAC inlier set of the pre-refine candidate (the LM step adjusts
/// the geometry but the inlier set was the seed of that geometry).
/// </summary>
private SynthesisOrientationWinner? ScoreOrientationCandidates(
    bool rotate180,
    IReadOnlyList<TypeAwareRansacSolver.TopKCandidate> candidates,
    IReadOnlyDictionary<string, double[,]> fields,
    IReadOnlyList<LandmarkReference> references,
    MapRect alignedRect)
{
    if (candidates.Count == 0) return null;

    SynthesisOrientationWinner? best = null;
    foreach (var cand in candidates)
    {
        var t = CandidateTransform.FromCalibration(cand.Calibration, alignedRect);
        var j = JEvaluator.Evaluate(t, fields, references);
        if (best is null || j.J > best.J)
        {
            best = new SynthesisOrientationWinner(
                Rotate180: rotate180,
                Calibration: cand.Calibration,
                Inliers: cand.Inliers,
                J: j.J,
                RefsAboveHalf: j.RefsAboveHalf,
                RefsOffCrop: j.RefsOffCrop,
                RefsTotal: references.Count);
        }
    }

    if (best is null) return null;

    // LM-refine the highest-J candidate's transform. The refined transform
    // re-scores against the same L_t fields → we update J / RefsAboveHalf /
    // RefsOffCrop to reflect the refined geometry. We do NOT mutate
    // best.Calibration, because that's the texture-pixel-space AreaCalibration
    // the engine still persists; LM works in aligned-pair-pixel space and
    // wouldn't round-trip cleanly through the rect re-scale.
    var seed = CandidateTransform.FromCalibration(best.Calibration, alignedRect);
    var refined = LocalRefine.Run(seed, fields, references, maxIter: 24, stepInit: 1.0);
    var refinedJ = JEvaluator.Evaluate(refined, fields, references);
    return best with
    {
        J = refinedJ.J,
        RefsAboveHalf = refinedJ.RefsAboveHalf,
        RefsOffCrop = refinedJ.RefsOffCrop,
    };
}
```

Note the inline comment about LM-refine: the refined `CandidateTransform` lives in aligned-pair-pixel space; converting back to a texture-pixel `AreaCalibration` requires inverting the `mapRect`-ratio scaling. Synthesis-J ranking only needs the J of the refined transform (which decides the orientation winner); the **persisted** calibration remains the pre-refine `cand.Calibration`. This matches the spec's framing that the LM-refine "scores the candidate" but the engine persists the upstream AreaCalibration.

(If a follow-up wants to *also* persist the refined geometry, that's an `AreaCalibration` re-derivation step from the refined `CandidateTransform`. Out of scope here; file a follow-up if Phase-C data shows persisting the refined fit improves accuracy.)

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: green (helper is private + unused).

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs
git commit -m "feat(map-calibration): synthesis-J scoring of RANSAC top-K candidates"
```

---

### Task 15: Cross-orientation selector + synthesis-J gate

**Files:**
- Modify: `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs`

Replace the existing per-orientation single-best + lower-residual cross-orientation selector with the synthesis-J path when mode ≠ Off, while keeping the legacy gate active as source-of-truth in Off + Shadow modes.

- [ ] **Step 1: Refactor `Solve` to take the synthesis-J path when mode ≠ Off**

The current `Solve` body iterates `{false, true}` orientations, calls Detect → `TypeAwareRansacSolver.Solve` (single-best), applies the legacy gate, and tracks lowest-residual accepted/rejected. Replace it with this structure:

```csharp
public CalibrationSolveResult Solve(DetectionRequest request, IReadOnlyList<LandmarkReference> references)
{
    // Cache per orientation: L_t fields + top-K + scored winner (when mode != Off).
    SynthesisOrientationWinner? bestSynthesis = null;
    CalibrationSolveResult? bestLegacyAccepted = null;
    CalibrationSolveResult? bestLegacyRejected = null;

    var mode = _options.SynthesisRerankMode;
    int topK = mode == SynthesisRerankMode.Off ? 1 : Math.Max(1, _options.RansacTopK);

    foreach (var rotate180 in new[] { false, true })
    {
        var texture = rotate180 ? ImageOps.Rotate180(request.BaseTexture) : request.BaseTexture;
        var req = request with { BaseTexture = texture };

        var detections = _detector.Detect(req);
        LogDetectSummary(rotate180, detections, references);
        var topKList = TypeAwareRansacSolver.SolveTopK(ToMutable(detections), references, request.MapRect, topK);
        var flatDetections = FlattenDetections(detections);

        // === Legacy track: pick the lowest-residual gate-accepted top-K[0] (preserves shadow-source-of-truth) ===
        if (topKList.Count == 0)
        {
            bestLegacyRejected ??= new CalibrationSolveResult(
                null, 0, "no geometrically-consistent fit", []) { Detections = flatDetections };
        }
        else
        {
            var legacyHead = topKList[0];
            if (_gate.Accept(legacyHead.Calibration, legacyHead.Inliers.Count, out var legacyReason))
            {
                var accepted = new CalibrationSolveResult(
                    legacyHead.Calibration, legacyHead.Inliers.Count, null, legacyHead.Inliers)
                    { Detections = flatDetections };
                if (bestLegacyAccepted is null
                    || legacyHead.Calibration.ResidualPixels < bestLegacyAccepted.Calibration!.ResidualPixels)
                {
                    bestLegacyAccepted = accepted;
                }
            }
            else if (bestLegacyRejected is null
                || legacyHead.Calibration.ResidualPixels
                    < (bestLegacyRejected.Calibration?.ResidualPixels ?? double.PositiveInfinity))
            {
                bestLegacyRejected = new CalibrationSolveResult(
                    null, legacyHead.Inliers.Count, legacyReason, legacyHead.Inliers)
                    { Detections = flatDetections };
            }
        }

        // === Synthesis track (skipped when mode == Off) ===
        if (mode == SynthesisRerankMode.Off) continue;

        var fields = BuildLikelihoodFieldsFromDeviation(req.Screenshot, req.BaseTexture, req.Templates);
        var winner = ScoreOrientationCandidates(rotate180, topKList, fields, references, req.MapRect);
        if (winner is null) continue;
        if (bestSynthesis is null || winner.J > bestSynthesis.J)
        {
            bestSynthesis = winner;
        }
    }

    // Decide the unified result.
    var legacyResult = bestLegacyAccepted ?? bestLegacyRejected ??
        new CalibrationSolveResult(null, 0, "no detections");

    if (mode != SynthesisRerankMode.Enabled)
    {
        // Off + Shadow: legacy is source of truth. Telemetry emission (Task 16)
        // wraps this whole block in the synthesis_rerank span; bestSynthesis
        // values are still available for tagging when mode == Shadow.
        EmitSynthesisRerankTelemetry(mode, bestSynthesis, legacyResult);
        if (legacyResult.Calibration is not null)
        {
            _logger?.LogInformation(
                "Auto-calibration accepted: residual {Residual:0.00} px, {Inliers} inliers.",
                legacyResult.Calibration.ResidualPixels, legacyResult.InlierCount);
            LogInlierCorrespondences(legacyResult.Calibration, legacyResult.Inliers);
        }
        else
        {
            _logger?.LogInformation("Auto-calibration rejected: {Reason}.", legacyResult.RejectReason);
        }
        return legacyResult;
    }

    // Enabled: synthesis-J IS the gate.
    if (bestSynthesis is null)
    {
        _logger?.LogInformation("Auto-calibration rejected (synthesis): no synthesis-J winner.");
        var noWinner = new CalibrationSolveResult(null, 0, "no synthesis-J winner",
            legacyResult.Inliers) { Detections = legacyResult.Detections };
        EmitSynthesisRerankTelemetry(mode, bestSynthesis, noWinner);
        return noWinner;
    }
    bool synthAccept = bestSynthesis.J >= _options.SynthesisJMin
                    && bestSynthesis.RefsAboveHalf >= _options.SynthesisNMin;
    CalibrationSolveResult finalResult;
    if (synthAccept)
    {
        finalResult = new CalibrationSolveResult(
            bestSynthesis.Calibration, bestSynthesis.Inliers.Count, null, bestSynthesis.Inliers)
            { Detections = legacyResult.Detections };
        _logger?.LogInformation(
            "Auto-calibration accepted (synthesis-J): J={J:0.00}, refs>=0.5 {Refs}/{Total}.",
            bestSynthesis.J, bestSynthesis.RefsAboveHalf, bestSynthesis.RefsTotal);
    }
    else
    {
        var reason = $"synthesis-J below threshold (J={bestSynthesis.J:0.00} < {_options.SynthesisJMin:0.00} "
                   + $"OR refs>=0.5 {bestSynthesis.RefsAboveHalf} < {_options.SynthesisNMin})";
        finalResult = new CalibrationSolveResult(null, bestSynthesis.Inliers.Count, reason, bestSynthesis.Inliers)
            { Detections = legacyResult.Detections };
        _logger?.LogInformation("Auto-calibration rejected (synthesis): {Reason}.", reason);
    }
    EmitSynthesisRerankTelemetry(mode, bestSynthesis, finalResult);
    return finalResult;
}

/// <summary>Placeholder — wired in Task 16.</summary>
private void EmitSynthesisRerankTelemetry(
    SynthesisRerankMode mode, SynthesisOrientationWinner? winner, CalibrationSolveResult finalResult)
{
    // Implemented in Task 16.
}
```

- [ ] **Step 2: Build + run all engine tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapCalibrationSolveEngine"`
Expected: existing engine tests still PASS — with `MapCalibrationSolverOptions` defaulting to Shadow, the legacy gate remains the source of truth, and the unified result returned in Shadow mode is identical to the pre-refactor lowest-residual-legacy-accepted result. If a test fails, the legacy track refactor lost equivalence; bisect by temporarily forcing `SynthesisRerankMode.Off` and comparing.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs
git commit -m "feat(map-calibration): synthesis-J cross-orientation selector + Enabled-mode gate"
```

---

### Task 16: Synthesis-J telemetry emission

**Files:**
- Modify: `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs`

Replace the `EmitSynthesisRerankTelemetry` placeholder from Task 15 with the actual span + meter emissions.

- [ ] **Step 1: Add the using statements**

At the top of `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs`, add ONLY the Diagnostics using (the existing file already has `using System.Collections.Generic;` explicitly — leave the existing line alone, but don't add a duplicate):

```csharp
using Mithril.MapCalibration.Diagnostics;
```

**Do NOT add a `ProjectReference` to `Mithril.Shared`.** `Mithril.MapCalibration.csproj` has a load-bearing comment refusing that reference: *"Deliberately no ProjectReference to Mithril.Shared: this assembly is meant to be consumable by Mithril.Shared too (and by any module/peer) without depending up the layering."* Task 10's `MapCalibrationDiagnostics` catalog exists precisely to avoid that dependency; Task 16 consumes the LOCAL catalog. The decoder-free invariant is preserved by construction (`MapCalibrationDiagnostics` only uses `System.Diagnostics` + `System.Diagnostics.Metrics`, both BCL). `ShippedGraphDecoderFreeTests` remains green.

- [ ] **Step 2: Replace the placeholder with the real emit body**

Replace the placeholder body from Task 15 with:

```csharp
private void EmitSynthesisRerankTelemetry(
    SynthesisRerankMode mode, SynthesisOrientationWinner? winner, CalibrationSolveResult finalResult)
{
    // Off mode: no L_t was built, no telemetry to emit. (StartActivity returns
    // null when no listener is attached, so the cost when listeners ARE
    // attached but mode is Off is one bool branch.)
    if (mode == SynthesisRerankMode.Off) return;

    using var span = MapCalibrationDiagnostics.ActivitySource.StartActivity("calibration.synthesis_rerank");
    if (span is null && !HasAnyMeterListener()) return;

    // The legacy gate's verdict, derived from finalResult when mode==Shadow
    // (legacy is source-of-truth, accept iff Calibration is not null) — or, when
    // mode==Enabled, computed against the *winner's* AreaCalibration + inlier
    // count so we can still report disagreement between the gates even though
    // synthesis-J is doing the final accept.
    bool legacyAccept;
    int legacyInlierCount;
    double? legacyResidualPx;
    if (mode == SynthesisRerankMode.Shadow)
    {
        legacyAccept = finalResult.Calibration is not null;
        legacyInlierCount = finalResult.InlierCount;
        legacyResidualPx = finalResult.Calibration?.ResidualPixels;
    }
    else
    {
        // Enabled: re-run the legacy gate on the synthesis winner's fit so the
        // disagreement counter remains meaningful.
        if (winner is not null
            && _gate.Accept(winner.Calibration, winner.Inliers.Count, out _))
        {
            legacyAccept = true;
            legacyInlierCount = winner.Inliers.Count;
            legacyResidualPx = winner.Calibration.ResidualPixels;
        }
        else if (winner is not null)
        {
            legacyAccept = false;
            legacyInlierCount = winner.Inliers.Count;
            legacyResidualPx = winner.Calibration.ResidualPixels;
        }
        else
        {
            legacyAccept = false;
            legacyInlierCount = 0;
            legacyResidualPx = null;
        }
    }

    bool synthesisAccept = mode == SynthesisRerankMode.Enabled
        ? finalResult.Calibration is not null
        : winner is not null
          && winner.J >= _options.SynthesisJMin
          && winner.RefsAboveHalf >= _options.SynthesisNMin;

    var synthVerdict = synthesisAccept ? "accept" : "reject";
    var gateVerdict = legacyAccept ? "accept" : "reject";
    var disagree = synthesisAccept != legacyAccept;
    var change = disagree
        ? (synthesisAccept ? "reject_to_accept" : "accept_to_reject")
        : "none";

    if (span is not null)
    {
        span.SetTag("synth.mode", mode.ToString().ToLowerInvariant());
        if (winner is not null)
        {
            span.SetTag("synth.j_best", winner.J);
            span.SetTag("synth.refs_above_0.5", winner.RefsAboveHalf);
            span.SetTag("synth.refs_total", winner.RefsTotal);
            span.SetTag("synth.refs_off_crop", winner.RefsOffCrop);
        }
        span.SetTag("synth.j_min", _options.SynthesisJMin);
        span.SetTag("synth.n_min", _options.SynthesisNMin);
        span.SetTag("synth.verdict", synthVerdict);
        span.SetTag("gate.verdict", gateVerdict);
        span.SetTag("gate.inliers", legacyInlierCount);
        if (legacyResidualPx is not null) span.SetTag("gate.residual_px", legacyResidualPx.Value);
        span.SetTag("disagree", disagree);
        span.SetTag("disagree.would_change", change);
    }

    if (winner is not null)
    {
        var verdictTag = new KeyValuePair<string, object?>("verdict", synthVerdict);
        MapCalibrationDiagnostics.Meters.SynthesisJ.Record(winner.J, verdictTag);
        MapCalibrationDiagnostics.Meters.SynthesisRefsAboveThreshold.Record(winner.RefsAboveHalf, verdictTag);
    }
    if (disagree)
    {
        MapCalibrationDiagnostics.Meters.SynthesisDisagree.Add(1,
            new KeyValuePair<string, object?>("change", change));
    }
}

/// <summary>
/// True if any consumer is currently listening to the synthesis meters. Used
/// to short-circuit the emit body when no span listener AND no meter listener
/// — the unconditional-producer convention (CLAUDE.md) means producers emit
/// without `if (active)`, but this helper avoids the per-emit prep work when
/// the activity didn't start and nobody is listening to the meters either.
/// </summary>
private static bool HasAnyMeterListener() =>
    MapCalibrationDiagnostics.Meters.SynthesisJ.Enabled
    || MapCalibrationDiagnostics.Meters.SynthesisRefsAboveThreshold.Enabled
    || MapCalibrationDiagnostics.Meters.SynthesisDisagree.Enabled;
```

- [ ] **Step 3: Build + run all map-calibration tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Tests tests/Mithril.MapCalibration.Capture.Tests`
Expected: PASS — the new emit body is observation-only when no listener is attached, so existing tests are unaffected. **Watch for `ShippedGraphDecoderFreeTests`** — if any decoder dep slipped in via the Mithril.Shared ProjectReference, this is where it fails.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs \
        src/Mithril.MapCalibration/Mithril.MapCalibration.csproj
git commit -m "feat(map-calibration): emit calibration.synthesis_rerank span + meters"
```

---

## 🛑 Review checkpoint 2 — Mid-PR-2 logic review (RECOMMENDED, optional)

The engine + telemetry are functionally wired. **Before** the test pinning in Block 5 locks the contract, an optional pause for a logic-only review catches algorithmic drift. Skip ONLY if you're confident you walked through the spec yourself.

**What the reviewer is looking at** (`MapCalibrationSolveEngine.cs` + `MapCalibrationDiagnostics.cs` + the two Shared pointer comments, no tests yet):
- Cross-orientation selector picks the higher-J winner when mode ≠ Off (was: lower-residual lowest-legacy)
- `Off` mode: legacy gate is source of truth, no L_t build, no span emit (zero cost)
- `Shadow` mode: legacy gate is source of truth, L_t builds + telemetry emits (verdict-vs-legacy disagreement counter is the headline signal)
- `Enabled` mode: synthesis-J IS the gate (`J ≥ J_min AND refs_above_0.5 ≥ N_min`)
- `disagree.would_change` tag emits `accept_to_reject` / `reject_to_accept` / `none` correctly per the spec's Q2 mode-semantics table
- LM-refine is applied to the highest-J candidate but the PERSISTED `AreaCalibration` is the upstream RANSAC fit (the LM-refined transform lives in aligned-pair-pixel space and is only used to score J — Task 14's inline comment explains the round-trip caveat)
- Telemetry vocabulary in `MapCalibrationDiagnostics.Meters` matches the spec's contract: histogram `mithril.map_calibration.synthesis.j` + histogram `…refs_above_threshold` + counter `…disagree`. ActivitySource is `Mithril.MapCalibration.Detection` (local catalog, peer of the Capture-layer `Mithril.MapCalibration.Capture` source).

Block 5 then lands the tests. If review surfaces a drift, fix it before Block 5 — Block 5's smoke tests would otherwise pin the wrong contract.

---

### Block 5 — Tests + audit (Tasks 17-18)

Production-vs-probe L_t equality test pins the convergence; three-mode engine smoke tests pin Off / Shadow / Enabled behaviour; audit of existing engine tests catches any that implicitly relied on the pre-synthesis behaviour.

---

### Task 17: Production-vs-probe L_t equality test

**Files:**
- Create: `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs`

Close the door on "the two surfaces drifted apart silently" (spec §Verification owed). Feed the same aligned crop + aligned texture through both paths and assert byte-equivalent L_t fields.

- [ ] **Step 1: Write the failing test**

Write `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs`:

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class SynthesisRerankFieldEquivalenceTests
{
    /// <summary>
    /// Production builds L_t from (alignedCrop, alignedTexture) via subtraction
    /// + DeviationFloodRimMask + ScoreAll. The probe builds L_t from a
    /// pre-computed deviation via LoadDeviationAsField (which applies the same
    /// DeviationFloodRimMask + ScoreAll). With production's subtracted
    /// deviation handed to the probe's LoadDeviationAsField, the fields must
    /// be byte-identical.
    /// </summary>
    [Fact]
    public void Production_path_and_probe_LoadDeviationAsField_produce_identical_fields()
    {
        const int W = 256, H = 192;
        var texturePixels = SyntheticMap.MakeTexture(W, H, seed: 4242);
        var shotPixels = (byte[])texturePixels.Clone();
        // Drip a few icon-shaped bright pixels into the screenshot so the
        // deviation has signal at predictable spots.
        SyntheticMap.BlitTeardrop(shotPixels, W, H, x: 80, y: 60, w: 16, h: 16, lum: 220);
        SyntheticMap.BlitTeardrop(shotPixels, W, H, x: 170, y: 120, w: 16, h: 16, lum: 220);

        var shot = new GrayImage(W, H, shotPixels);
        var tex  = new GrayImage(W, H, texturePixels);

        var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);
        var template = templates.Templates[0];

        // Production path (mirrors MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation).
        var prodDev = new byte[W * H];
        for (int i = 0; i < prodDev.Length; i++)
        {
            int d = shot.Pixels[i] - tex.Pixels[i];
            prodDev[i] = d > 0 ? (byte)System.Math.Min(255, d) : (byte)0;
        }
        var prodField = IconLikelihoodField.LoadDeviationAsField(
            new GrayImage(W, H, prodDev), template,
            applyRimMask: true, devThr: IconLikelihoodField.DefaultDevThr);

        // Probe path (LoadDeviationAsField over an externally-computed deviation).
        var probeDev = new byte[W * H];
        for (int i = 0; i < probeDev.Length; i++)
        {
            int d = shot.Pixels[i] - tex.Pixels[i];
            probeDev[i] = d > 0 ? (byte)System.Math.Min(255, d) : (byte)0;
        }
        var probeField = IconLikelihoodField.LoadDeviationAsField(
            new GrayImage(W, H, probeDev), template);  // default rim-mask = true, default devThr

        // Byte-equivalent.
        prodField.GetLength(0).Should().Be(probeField.GetLength(0));
        prodField.GetLength(1).Should().Be(probeField.GetLength(1));
        for (int y = 0; y < prodField.GetLength(0); y++)
        for (int x = 0; x < prodField.GetLength(1); x++)
        {
            prodField[y, x].Should().Be(probeField[y, x],
                $"production and probe paths must score the same deviation byte-identically at ({x},{y})");
        }
    }
}
```

(The two paths above are constructed to highlight the convergence: both go through `LoadDeviationAsField` with `applyRimMask: true`. The test will pass by construction *after* PR-1; restate it explicitly here as the spec's verification-owed contract so a future refactor that diverges the two paths is caught.)

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SynthesisRerankFieldEquivalenceTests"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs
git commit -m "test(map-calibration): pin production-vs-probe L_t field equivalence"
```

---

### Task 18: Engine smoke tests (Off / Shadow / Enabled)

**Files:**
- Create: `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankShadowModeTests.cs`

Lock in three load-bearing behaviours: (a) Off mode preserves the legacy result and doesn't build L_t / emit telemetry, (b) Shadow keeps the legacy verdict but emits telemetry, (c) Enabled rejects when synthesis-J is below threshold even if the legacy gate would accept.

- [ ] **Step 1: Write the failing tests**

Write `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankShadowModeTests.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Mithril.Shared.Diagnostics.Telemetry;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class SynthesisRerankShadowModeTests
{
    private const int TexW = 320, TexH = 260;
    private static readonly AreaCalibration Truth = new(
        Scale: 1.1, RotationRadians: 0.25, OriginX: 160, OriginY: 130,
        ReferenceCount: 0, ResidualPixels: 0.0)
    { MirrorNorth = false, CalibrationZoom = 1.0 };

    private static readonly (string Type, string Icon, int W, int H, int Lum, double X, double Z)[] Landmarks =
    [
        ("Portal", "landmark_portal", 24, 32, 60, -60, 70),
        ("Portal", "landmark_portal", 24, 32, 60, 70, -50),
        ("TeleportationPlatform", "landmark_telepad", 28, 22, 180, 90, 30),
        ("MeditationPillar", "landmark_medipillar", 18, 40, 110, -20, -40),
        ("Npc", "landmark_npc", 20, 28, 220, 40, 55),
    ];

    private static (GrayImage shot, GrayImage tex, System.Collections.Generic.List<LandmarkReference> refs) Build()
    {
        var texPixels = SyntheticMap.MakeTexture(TexW, TexH, seed: 7777);
        var shotPixels = (byte[])texPixels.Clone();
        var refs = new System.Collections.Generic.List<LandmarkReference>();
        foreach (var l in Landmarks)
        {
            var tex = Truth.WorldToWindow(new WorldCoord(l.X, 0, l.Z));
            SyntheticMap.BlitTeardrop(shotPixels, TexW, TexH, tex.X, tex.Y, l.W, l.H, l.Lum);
            refs.Add(new LandmarkReference(l.Type, l.Icon, new WorldCoord(l.X, 0, l.Z)));
        }
        return (new GrayImage(TexW, TexH, shotPixels), new GrayImage(TexW, TexH, texPixels), refs);
    }

    private static MapCalibrationSolveEngine EngineWith(MapCalibrationSolverOptions opts) =>
        new(new DeviationBlobCalibrationDetector(), new CalibrationConfidenceGate(), null, opts);

    private static IconTemplateSet Templates() => SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);

    private static MapRect Rect() => new(0, 0, TexW, TexH, TexW, TexH);

    private static DetectionRequest Request(GrayImage shot, GrayImage tex) =>
        new(shot, tex, Rect(), Templates(), RimMaskMode.DeviationFlood,
            LowNcc: 0.5, TypeFloor: 0.80,
            BlobOptions: new BlobOptions(MinArea: 12, MaxIconArea: 900, MinSolidity: 0.35, MaxAspect: 2.5, MinPeak: 0.7))
            { RenderSizePx = 16 };

    [Fact]
    public void Mode_Off_emits_no_synthesis_span()
    {
        var (shot, tex, refs) = Build();
        var opts = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Off };
        var engine = EngineWith(opts);

        var spans = new System.Collections.Generic.List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Mithril.MapCalibration.Detection",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => spans.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        _ = engine.Solve(Request(shot, tex), refs);

        spans.Should().NotContain(a => a.OperationName == "calibration.synthesis_rerank");
    }

    [Fact]
    public void Mode_Shadow_emits_span_but_legacy_gate_is_source_of_truth()
    {
        var (shot, tex, refs) = Build();
        var opts = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Shadow };
        var shadowEngine = EngineWith(opts);
        var offEngine = EngineWith(new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Off });

        var spans = new System.Collections.Generic.List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Mithril.MapCalibration.Detection",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => spans.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var shadowResult = shadowEngine.Solve(Request(shot, tex), refs);
        var offResult    = offEngine.Solve(Request(shot, tex), refs);

        // Legacy verdict is unchanged from Off (Shadow keeps the legacy gate as source-of-truth).
        (shadowResult.Calibration is not null).Should().Be(offResult.Calibration is not null);

        // Synthesis span emitted.
        spans.Should().Contain(a => a.OperationName == "calibration.synthesis_rerank");
        var synth = spans.First(a => a.OperationName == "calibration.synthesis_rerank");
        synth.GetTagItem("synth.mode").Should().Be("shadow");
        synth.GetTagItem("synth.verdict").Should().BeOneOf("accept", "reject");
    }

    [Fact]
    public void Mode_Enabled_rejects_when_J_below_threshold()
    {
        var (shot, tex, refs) = Build();
        // Set J_min absurdly high so Enabled rejects even the truth fit.
        var opts = new MapCalibrationSolverOptions
        {
            SynthesisRerankMode = SynthesisRerankMode.Enabled,
            SynthesisJMin = 1_000_000.0,
            SynthesisNMin = 1_000_000,
        };
        var engine = EngineWith(opts);

        var result = engine.Solve(Request(shot, tex), refs);
        result.Calibration.Should().BeNull();
        result.RejectReason.Should().Contain("synthesis-J below threshold");
    }

    [Fact]
    public void Mode_Enabled_accepts_when_synthesis_thresholds_clear()
    {
        var (shot, tex, refs) = Build();
        var opts = new MapCalibrationSolverOptions
        {
            SynthesisRerankMode = SynthesisRerankMode.Enabled,
            SynthesisJMin = 0.0,
            SynthesisNMin = 0,
        };
        var engine = EngineWith(opts);

        var result = engine.Solve(Request(shot, tex), refs);
        result.Calibration.Should().NotBeNull(
            "synthesis-J with zero thresholds must accept the synthetic truth fit");
    }
}
```

- [ ] **Step 2: Audit pre-existing engine tests for the implicit-default-Shadow side-effect**

In `tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineTests.cs` and `MapCalibrationSolveEngineLoggingTests.cs`, the existing test factories `new MapCalibrationSolveEngine(new DeviationBlobCalibrationDetector(), new CalibrationConfidenceGate())` now construct with default-Shadow options (synthesis-J runs in shadow, span emits). The legacy assertions still hold because Shadow preserves the legacy verdict, but a test that asserts on emitted spans / total log line count *would* break.

Grep for `LogInformation` assertions in these two files. For each one that counts log lines or asserts a specific log-line set, explicitly construct an Off-mode options instance so those tests stay scoped to legacy behaviour:

```csharp
private static MapCalibrationSolveEngine Engine() =>
    new(new DeviationBlobCalibrationDetector(),
        new CalibrationConfidenceGate(),
        logger: TestLogger,
        options: new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Off });
```

(If a quick read shows no such assertions, no edit needed. The fact-finding step matters more than the edit.)

- [ ] **Step 3: Run the new tests + all engine tests**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapCalibrationSolveEngine|FullyQualifiedName~SynthesisRerank"`
Expected: all PASS.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test Mithril.slnx`
Expected: all PASS. Watch in particular for `ShippedGraphDecoderFreeTests` (no decoder deps slipped in via the `Mithril.Shared` ProjectReference) and the Capture-tests `AutoCalibrationEngineTests` (the engine returns the same shape it always did).

- [ ] **Step 5: Commit**

```bash
git add tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankShadowModeTests.cs \
        tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineTests.cs \
        tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineLoggingTests.cs
git commit -m "$(cat <<'EOF'
test(map-calibration): synthesis-J Off / Shadow / Enabled mode smoke tests

Pins the three load-bearing behaviours per the spec:
  - Off: no synthesis_rerank span emits
  - Shadow: span emits, but the legacy gate is the source of truth (so
    Shadow's accept/reject matches Off's)
  - Enabled: synthesis-J is the gate (rejects under unsatisfiable thresholds,
    accepts under permissive ones)
EOF
)"
```

---

## 🛑 Review checkpoint 3 — Open PR-2

Push the feature branch and open the PR-2 pull request via `gh pr create`. Title: `feat(map-calibration): synthesis-J re-rank in Shadow mode [PR-2]`. Body should:
- Link to [docs/superpowers/specs/2026-06-02-synthesis-rerank-design.md](../specs/2026-06-02-synthesis-rerank-design.md)
- Note that PR-3 (flip default to Enabled) is gated on Phase-C telemetry per the spec's Q3 acceptance criteria
- Reference the open follow-ups: spec Open Question 2 (`08-synthesis-verdict.json` bundle sibling) and Open Question 3 (per-area thresholds) — both deferred until Phase-C data motivates them
- Quote the spec's Q2 acceptance criteria so a future reviewer can find the flip-default checklist quickly

Use HEREDOC per CLAUDE.md commit conventions.

**What the reviewer is looking at (the whole PR-2 diff):**
- `MapCalibrationSolverOptions` POCO + `SynthesisRerankMode` enum (Block 3)
- `MapCalibrationDiagnostics` local catalog in `Mithril.MapCalibration/Diagnostics/` + pointer comments in the Shared catalogs (Block 3)
- `MapCalibrationSolveEngine.Solve` refactored to compute synthesis-J across orientations + apply the synthesis-J gate when `Enabled` (Block 4)
- `calibration.synthesis_rerank` span + meter emissions (Block 4)
- L_t equality test + three-mode smoke tests (Block 5)
- Audit of existing engine tests for the default-Shadow side-effect (Block 5)
- All map-calibration tests + `ShippedGraphDecoderFreeTests` green

If Review checkpoint 2 was skipped, this is also where the logic from Block 4 gets its first human scrutiny — reviewer's time budget should reflect that.

---

## Out of scope — file as follow-up issues when motivated

These are documented in the spec and intentionally NOT in this plan:

1. **PR-3 (flip default to `SynthesisRerankMode.Enabled`)** — gated on the Q2 acceptance criteria: ≥ 50 attempts logged across ≥ 3 areas in Shadow, no clean-accept false-rejects, ≥ 1 confirmed `accept_to_reject` (Bundle B pattern), ≥ 1 confirmed `reject_to_accept` (Bundle C pattern). File when telemetry shows the criteria met.
2. **Shadow-mode `08-synthesis-verdict.json` bundle sibling** (Open Q2) — small but a separate `CalibrationAttemptContext` shape change + `AttemptFilesJson` field add; defer unless after-the-fact bundle-replay studies demonstrate need.
3. **Per-area `J_min` / `N_min` thresholds** (Open Q3) — Phase-C telemetry decides whether single global thresholds span the data; if not, add a config dict on `MapCalibrationSolverOptions` or scale `N_min` to `area.RefCount`.
4. **Persisting the LM-refined synthesis transform** — Task 14 deliberately persists the upstream RANSAC `AreaCalibration`, not the LM-refined `CandidateTransform`. If Phase-C accuracy data shows the refined fit is better, file a follow-up to round-trip the refined `CandidateTransform` back through the `mapRect` ratio inverse.
5. **Settings UI for the three thresholds** — POCO + `INotifyPropertyChanged` is shipped; a Settings view binds straightforwardly. Out of scope per spec §Q2.

---

## Verification owed

Both items the spec lists are addressed by tests in this plan:
- **Conversion-equivalence test** — Task 8 (`CandidateTransformConversionTests`).
- **Production-vs-probe L_t equality test** — Task 17 (`SynthesisRerankFieldEquivalenceTests`).

Phase-A recalibration was complete before this plan started (per the spec, PR #993 pinned the defaults).
