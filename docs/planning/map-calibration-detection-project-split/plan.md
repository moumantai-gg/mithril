# Map-calibration: extract detection into its own project — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract `Mithril.MapCalibration.Detection` as a new project; lift the `Detection/` folder and the OpenCv-using `FeatureMatchingRefiner` + ORB descriptor cache out of `.Capture` into it; promote contracts-tier types up to `Mithril.MapCalibration`; tighten the arch-test allowlist so OpenCv lives in exactly one named project.

**Architecture:** Pure refactor — file moves, namespace adjustments, csproj edits, DI extension split. No algorithm changes. Three-tier dependency: `.Capture → .Detection → .MapCalibration`. See [spec.md](spec.md) for full rationale and [spec.md §"Type placement"](spec.md#type-placement) for the rule that decides each type's home.

**Tech Stack:** .NET 10 (`net10.0-windows`), xunit + FluentAssertions, OpenCvSharp4 + OpenCvSharp4.runtime.win (allowlisted, named project only), Mithril.slnx solution.

---

## Working notes for the engineer

- **Build/test commands** (CLAUDE.md): `dotnet build Mithril.slnx`, `dotnet test Mithril.slnx`. Single test: `dotnet test tests/<Project> --filter "FullyQualifiedName~<Test>"`.
- **Mithril running blocks builds.** A pre-tool hook (`check-mithril-running.ps1`) refuses `dotnet build/test/publish/pack` while the shell process is alive. Close any running `Mithril.exe` before each build step. Memory: `mithril_build_file_lock_silent`. Also note that the hook may miss `.claude/worktrees/` paths in some configurations (`mithril_running_hook_misses_claude_worktrees`) — if a build silently appears to succeed but DLL mtimes don't move, manually close Mithril and retry.
- **LSP for find-references.** For each "rename namespace + update `using` statements" step below, the LSP tool's find-references / go-to-definition is the right way to enumerate consumers. Grep alone misses partial classes, source-generated `[ObservableProperty]` setters, and JSON contexts (CLAUDE.md). Use `ToolSearch query: "select:LSP"` to load its schema if not already loaded.
- **csproj `InternalsVisibleTo`.** Existing `.Capture` has `<InternalsVisibleTo Include="Mithril.MapCalibration.Capture.Tests" />`. New `.Detection` needs the same shape if any code lifted from `.Capture/Internal/` is referenced by `.Capture.Tests`. Add when (and only when) the compiler asks for it.
- **Commit cadence: one commit per phase, four phase commits per PR.** Each task within a phase ends with a `git add` (stage only). The phase commit then bundles all that phase's staged changes with a Conventional-Commits message per repo style (`refactor(map-calibration): …`, `chore(arch-test): …`). On merge GitHub squashes the four commits into one on `main`; the per-phase structure lives on the PR branch for reviewer ergonomics.
- **Squash-merge gc trap.** If a single PR adds + removes a file, the add gets GC'd ~90 days later. This plan is safe from that — every file move is `git mv` (rename, not add+delete). Memory: `squash_merge_orphans_netzero_plans`.

---

## File structure (target state, post-plan)

**New project:** `src/Mithril.MapCalibration.Detection/`
- `Mithril.MapCalibration.Detection.csproj` — references `Mithril.MapCalibration`; `PackageReference` to `OpenCvSharp4` + `OpenCvSharp4.runtime.win`; `<InternalsVisibleTo>` for test project (added when needed).
- `DependencyInjection/DetectionServiceCollectionExtensions.cs` — `AddMithrilMapCalibrationDetection()`; registers `IIconTemplateProvider`, `IBaseTextureProvider`, `ICalibrationDetector`, `MapCalibrationSolveEngine`, `IMapRegionRefiner` (FeatureMatchingRefiner), `MapCalibrationLocateOptions`, `CachedOrbDescriptorProvider`, `OrbDescriptorWriter`.
- All algorithm files lifted from `src/Mithril.MapCalibration/Detection/` (NCC, blob, morphology, flood masks, image ops, scaler, RANSAC, solve engine, J-evaluator, local refine, etc.).
- `FeatureMatchingRefiner.cs`, `CachedOrbDescriptorProvider.cs`, `OrbDescriptorWriter.cs`, `OrbDescriptorManifest.cs` — lifted from `src/Mithril.MapCalibration.Capture/{,Internal/}`.
- `Internal/` — manifests, hash gate, cached providers, sidecar result (lifted from `Detection/Internal/`).

**Promoted to contracts:** `src/Mithril.MapCalibration/`
- `MapRect.cs`, `LandmarkReference.cs`, `CandidateTransform.cs`, `CanonicalLandmarkTypes.cs` (from `Detection/`).
- `ICalibrationDetector.cs`, `ICalibrationConfidenceGate.cs`, `IBaseTextureProvider.cs`, `IIconTemplateProvider.cs`, `IAssetExtractor.cs` (from `Detection/`).
- `IMapRegionRefiner.cs`, `MapRegionRefineResult.cs`, `LocateMetrics.cs` (from `.Capture`).
- `Internal/ProcessAssetExtractor.cs` (from `Detection/`).

**Shrunk:** `src/Mithril.MapCalibration/Detection/` — folder removed entirely after Phase C lift.

**Shrunk:** `src/Mithril.MapCalibration.Capture/`
- No more `OpenCvSharp` PackageReference.
- Removed: `FeatureMatchingRefiner.cs`, `Internal/CachedOrbDescriptorProvider.cs`, `Internal/OrbDescriptorWriter.cs`, `Internal/OrbDescriptorManifest.cs`, `IMapRegionRefiner.cs`, `MapRegionRefineResult.cs`, `LocateMetrics.cs`.
- `DependencyInjection/CaptureServiceCollectionExtensions.cs` — slimmed; OpenCv/ORB-related registrations move to `DetectionServiceCollectionExtensions`; this file calls `AddMithrilMapCalibrationDetection()`.
- Stale csproj comment header (`FindTransformECC`, `#978` exception) rewritten to drop OpenCv reference.

**Modified:** `tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs`
- `PackageAllowlistByProject` keys `Mithril.MapCalibration.Detection.csproj` instead of `Mithril.MapCalibration.Capture.csproj`.
- Class-level XML doc prose rewritten: names `.Detection` as the OpenCv home, drops the stale `FindTransformECC` mention.

**Modified:** `Mithril.slnx` — adds the new project row.

**Modified:** `src/Mithril.Shell/Mithril.Shell.csproj` — adds explicit `ProjectReference` to `Mithril.MapCalibration.Detection` per the shell-must-projectref convention (memory: `shell_must_projectref_shared_libs`).

---

## Phase A — Stand up the new project (Tasks 1–3)

### Task 1: Create `Mithril.MapCalibration.Detection` project skeleton

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj`
- Modify: `Mithril.slnx` (add project row)

**Working decision:** Task 1 creates the csproj WITHOUT OpenCv `PackageReference` lines (just the framework + DI deps), so the arch-test stays green at end of Task 1. Task 2 adds the OpenCv refs to the csproj AND adds the allowlist entry in the same commit — atomic introduction of OpenCv on the project.

- [ ] **Step 1: Create the csproj (no OpenCv yet)**

Write `src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>Mithril.MapCalibration.Detection</RootNamespace>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <!-- OpenCvSharp PackageReferences added in Task 2 alongside the arch-test
         allowlist edit, so the arch-test stays green at every commit. -->
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Mithril.MapCalibration\Mithril.MapCalibration.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add to Mithril.slnx**

In `Mithril.slnx`, inside `<Folder Name="/src/">`, insert (alphabetical, after `Mithril.MapCalibration.Capture`):

```xml
    <Project Path="src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj" />
```

Final ordering in that folder section:

```xml
    <Project Path="src/Mithril.MapCalibration/Mithril.MapCalibration.csproj" />
    <Project Path="src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj" />
    <Project Path="src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj" />
    <Project Path="src/Mithril.Overlay/Mithril.Overlay.csproj" />
```

- [ ] **Step 3: Build + tests green (stage; commit lands at end of Phase A)**

Run: `dotnet build Mithril.slnx && dotnet test Mithril.slnx`
Expected: succeeds. The new project has no OpenCv reference yet, so the arch-test passes.

Stage the changes — single PR-wide commit at the end of each phase:

```bash
git add src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj Mithril.slnx
```

---

### Task 2: Add OpenCv PackageReferences to `.Detection` + allowlist (atomic)

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj`
- Modify: `tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs`

- [ ] **Step 1: Add OpenCv PackageReferences to `.Detection.csproj`**

In `src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj`, replace the placeholder-comment `<ItemGroup>` block with:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <!-- #978 / spec.md: OpenCv is permitted in the single dedicated CV math
         project. Allowlisted by ShippedGraphDecoderFreeTests.
         OpenCv is an alignment library, not an asset decoder; Mithril is
         Windows-only WPF (not trimmed/AOT) so an in-process call beats an
         out-of-process sidecar. Any OTHER src/** project taking an
         OpenCvSharp reference is a violation. -->
    <PackageReference Include="OpenCvSharp4" />
    <PackageReference Include="OpenCvSharp4.runtime.win" />
  </ItemGroup>
```

- [ ] **Step 2: Update `PackageAllowlistByProject`**

In [ShippedGraphDecoderFreeTests.cs:65-68](../../../tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs), update the dictionary to include the new project ALONGSIDE the existing `.Capture` entry. (The `.Capture` entry is removed later in Task 10 — for now both projects can have OpenCv during the intermediate build states.)

Replace:

```csharp
    private static readonly Dictionary<string, string[]> PackageAllowlistByProject = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mithril.MapCalibration.Capture.csproj"] = ["OpenCvSharp"],
    };
```

with:

```csharp
    private static readonly Dictionary<string, string[]> PackageAllowlistByProject = new(StringComparer.OrdinalIgnoreCase)
    {
        // Transitional state — `.Capture` keeps its entry until the OpenCv-using code
        // is lifted into `.Detection` (this plan, Task 10). The final state has only
        // `.Detection`.
        ["Mithril.MapCalibration.Capture.csproj"] = ["OpenCvSharp"],
        ["Mithril.MapCalibration.Detection.csproj"] = ["OpenCvSharp"],
    };
```

- [ ] **Step 3: Build + tests green (stage; commit at end of Phase A)**

Run: `dotnet build Mithril.slnx && dotnet test Mithril.slnx`
Expected: succeeds. `ShippedGraphDecoderFreeTests` is green because both `.Capture` (still has OpenCv pending Task 11) and `.Detection` (just gained OpenCv) are explicitly allowlisted.

```bash
git add src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs
```

---

### Task 3: Add `ProjectReference` to `.Detection` from `.Capture`, Shell, and capture-tests

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj`
- Modify: `src/Mithril.Shell/Mithril.Shell.csproj`
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj`

- [ ] **Step 1: Add reference in `.Capture.csproj`**

In `src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj`, the `<ItemGroup>` listing `<ProjectReference>` entries currently contains:

```xml
    <ProjectReference Include="..\Mithril.MapCalibration\Mithril.MapCalibration.csproj" />
    <ProjectReference Include="..\Mithril.Overlay\Mithril.Overlay.csproj" />
    <ProjectReference Include="..\Mithril.Shared\Mithril.Shared.csproj" />
    <ProjectReference Include="..\Arda\Arda.Contracts\Arda.Contracts.csproj" />
```

Insert (alphabetical position, after `Mithril.MapCalibration`):

```xml
    <ProjectReference Include="..\Mithril.MapCalibration.Detection\Mithril.MapCalibration.Detection.csproj" />
```

- [ ] **Step 2: Add reference in `Mithril.Shell.csproj`**

Per memory `shell_must_projectref_shared_libs`: any new `Mithril.*` library the shell needs at runtime requires an explicit project ref. In `src/Mithril.Shell/Mithril.Shell.csproj`, find the existing block at lines 91-96 (currently the `Mithril.MapCalibration` + `.Capture` references) and insert AFTER the `.Capture` line:

```xml
    <!-- Mithril.MapCalibration.Detection (spec: detection-project-split): OpenCv-using
         detection algorithms (NCC, blob, RANSAC, solve engine, FeatureMatchingRefiner).
         Shell must reference it so the DLL lands in the app base dir per
         shell_must_projectref_shared_libs convention. -->
    <ProjectReference Include="..\Mithril.MapCalibration.Detection\Mithril.MapCalibration.Detection.csproj" />
```

- [ ] **Step 3: Add reference in capture-tests csproj**

In `tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj`, after the existing `<ProjectReference Include="..\..\src\Mithril.MapCalibration.Capture\…" />`, add:

```xml
    <ProjectReference Include="..\..\src\Mithril.MapCalibration.Detection\Mithril.MapCalibration.Detection.csproj" />
```

(This anticipates the capture-tests reaching into types that get lifted to `.Detection` in later tasks. If after Task 9 the compiler shows this reference is never needed, leave it — capture tests on the detection-tier classes are a feature, not a leak.)

- [ ] **Step 4: Build green**

Run: `dotnet build Mithril.slnx`
Expected: succeeds.

- [ ] **Step 5: Tests green (stage; commit at end of Phase A)**

Run: `dotnet test Mithril.slnx`
Expected: all green.

```bash
git add src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj src/Mithril.Shell/Mithril.Shell.csproj tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj
```

---

### Phase A commit

- [ ] **Verify everything for Phase A is staged**

```bash
git status
```

Expected: staged changes include the new `.Detection` csproj, `Mithril.slnx`, arch-test, capture csproj, shell csproj, capture-tests csproj.

- [ ] **Commit Phase A**

```bash
git commit -m "$(cat <<'EOF'
refactor(map-calibration): add .Detection project skeleton (Phase A)

- Create empty Mithril.MapCalibration.Detection project (net10.0-windows, BCL + OpenCvSharp)
- Add to Mithril.slnx
- Wire ProjectReference from .Capture, Mithril.Shell, and capture-tests
- Add .Detection to arch-test PackageAllowlistByProject alongside .Capture
  (transitional state — .Capture entry removed in Phase D)

Spec: docs/planning/map-calibration-detection-project-split/spec.md
Plan: docs/planning/map-calibration-detection-project-split/plan.md

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase B — Promote contracts-tier types (Tasks 4–7)

For every task in this phase, the move pattern is identical:

1. `git mv` the file to its new location.
2. Update the file's `namespace` declaration to the new tier.
3. Find consumers (LSP find-references on the type) and either (a) update their `using` statements, or (b) accept that the type is now in `Mithril.MapCalibration` which most consumers already `using`.
4. Build + test green.

### Task 4: Promote primitive domain types to contracts tier

**Types moved:** `MapRect`, `LandmarkReference`, `CandidateTransform`, `CanonicalLandmarkTypes`.

**Files:**
- Move: `src/Mithril.MapCalibration/Detection/MapRect.cs` → `src/Mithril.MapCalibration/MapRect.cs`
- Move: `src/Mithril.MapCalibration/Detection/LandmarkReference.cs` → `src/Mithril.MapCalibration/LandmarkReference.cs`
- Move: `src/Mithril.MapCalibration/Detection/CandidateTransform.cs` → `src/Mithril.MapCalibration/CandidateTransform.cs`
- Move: `src/Mithril.MapCalibration/Detection/CanonicalLandmarkTypes.cs` → `src/Mithril.MapCalibration/CanonicalLandmarkTypes.cs`

- [ ] **Step 1: Move files**

```bash
git mv src/Mithril.MapCalibration/Detection/MapRect.cs src/Mithril.MapCalibration/MapRect.cs
git mv src/Mithril.MapCalibration/Detection/LandmarkReference.cs src/Mithril.MapCalibration/LandmarkReference.cs
git mv src/Mithril.MapCalibration/Detection/CandidateTransform.cs src/Mithril.MapCalibration/CandidateTransform.cs
git mv src/Mithril.MapCalibration/Detection/CanonicalLandmarkTypes.cs src/Mithril.MapCalibration/CanonicalLandmarkTypes.cs
```

- [ ] **Step 2: Update each file's namespace declaration**

In each of the four moved files, change the namespace line:

From:
```csharp
namespace Mithril.MapCalibration.Detection;
```

To:
```csharp
namespace Mithril.MapCalibration;
```

(The remaining file content is unchanged.)

- [ ] **Step 3: Build to surface consumers**

Run: `dotnet build Mithril.slnx`
Expected: many CS0234 / CS0246 errors from consumers that referenced these types via `Mithril.MapCalibration.Detection.MapRect` etc.

- [ ] **Step 4: Fix consumers**

Each error points at a `using Mithril.MapCalibration.Detection;` that no longer needs that namespace for these four types. Two valid fixes per consumer:
- The consumer already has `using Mithril.MapCalibration;` (most do — it's the parent namespace where other contracts already live). In that case the `using Mithril.MapCalibration.Detection;` line stays only if the consumer also references other detection-internal types; otherwise delete it.
- The consumer doesn't have `using Mithril.MapCalibration;`. Add it.

Use the LSP tool (find-references on each of the four types) to enumerate consumers. Common consumer locations to check:
- `src/Mithril.MapCalibration.Capture/*.cs` (AutoCalibrationEngine, FeatureMatchingRefiner, ReferenceDataAreaReferenceProvider, IAreaReferenceProvider, IMapCalibrationSolver, Diagnostics/CalibrationBundleJson)
- `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs`
- `src/Mithril.MapCalibration/Detection/*.cs` (still in their old locations — these reference each other via the Detection namespace, which still contains the algorithm files; this should be a non-issue if their `using` statements stay)

- [ ] **Step 5: Build + tests green (stage; commit at end of Phase B)**

Run: `dotnet build Mithril.slnx && dotnet test Mithril.slnx`
Expected: all green.

```bash
git add -A
```

---

### Task 5: Promote boundary interfaces to contracts tier

**Types moved:** `ICalibrationDetector`, `ICalibrationConfidenceGate`, `IBaseTextureProvider`, `IIconTemplateProvider`, `IAssetExtractor`.

**Files:**
- Move: `src/Mithril.MapCalibration/Detection/ICalibrationDetector.cs` → `src/Mithril.MapCalibration/ICalibrationDetector.cs`
- Move: `src/Mithril.MapCalibration/Detection/ICalibrationConfidenceGate.cs` → `src/Mithril.MapCalibration/ICalibrationConfidenceGate.cs`
- Move: `src/Mithril.MapCalibration/Detection/IBaseTextureProvider.cs` → `src/Mithril.MapCalibration/IBaseTextureProvider.cs`
- Move: `src/Mithril.MapCalibration/Detection/IIconTemplateProvider.cs` → `src/Mithril.MapCalibration/IIconTemplateProvider.cs`
- Move: `src/Mithril.MapCalibration/Detection/IAssetExtractor.cs` → `src/Mithril.MapCalibration/IAssetExtractor.cs`

- [ ] **Step 1: Move files**

```bash
git mv src/Mithril.MapCalibration/Detection/ICalibrationDetector.cs src/Mithril.MapCalibration/ICalibrationDetector.cs
git mv src/Mithril.MapCalibration/Detection/ICalibrationConfidenceGate.cs src/Mithril.MapCalibration/ICalibrationConfidenceGate.cs
git mv src/Mithril.MapCalibration/Detection/IBaseTextureProvider.cs src/Mithril.MapCalibration/IBaseTextureProvider.cs
git mv src/Mithril.MapCalibration/Detection/IIconTemplateProvider.cs src/Mithril.MapCalibration/IIconTemplateProvider.cs
git mv src/Mithril.MapCalibration/Detection/IAssetExtractor.cs src/Mithril.MapCalibration/IAssetExtractor.cs
```

- [ ] **Step 2: Update each file's namespace**

In each of the five files, change `namespace Mithril.MapCalibration.Detection;` to `namespace Mithril.MapCalibration;`.

Watch for inter-interface references inside these files (e.g. `ICalibrationConfidenceGate` may reference `MapRect`, `CandidateTransform`). After Task 4 those types are already in `Mithril.MapCalibration` so the now-same-namespace references resolve without a `using`.

- [ ] **Step 3: Build, surface consumers, fix `using` statements**

Run: `dotnet build Mithril.slnx`

Common consumers to update via LSP find-references:
- `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs` (DI registrations of `ICalibrationDetector`, `ICalibrationConfidenceGate`, `IBaseTextureProvider`, `IIconTemplateProvider`) — the `using Mithril.MapCalibration.Detection;` at line 5 stays (still need the impl types from Detection); the interfaces resolve via the same namespace.
- `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` — `IAssetExtractor` registration; uses `using Mithril.MapCalibration.Detection;` at line 7 which may need to stay or change depending on what else the file uses from `.Detection`.
- `src/Mithril.MapCalibration/Detection/*.cs` impl files (`CalibrationConfidenceGate`, `DeviationBlobCalibrationDetector`, `CachedBaseTextureProvider`, `CachedIconTemplateProvider`, `ProcessAssetExtractor`) — their `implements ICalibrationDetector` etc. now references a parent-namespace type; the same-`Mithril.MapCalibration.Detection` namespace they live in already inherits this via the namespace hierarchy (or the file already has `using Mithril.MapCalibration;` via Task 4's needs). Add it if the compiler asks.

- [ ] **Step 4: Build + tests green (stage; commit at end of Phase B)**

Run: `dotnet build Mithril.slnx && dotnet test Mithril.slnx`
Expected: all green.

```bash
git add -A
```

---

### Task 6: Promote refiner contract types from `.Capture` to contracts tier

**Types moved:** `IMapRegionRefiner`, `MapRegionRefineResult`, `LocateMetrics`.

**Files:**
- Move: `src/Mithril.MapCalibration.Capture/IMapRegionRefiner.cs` → `src/Mithril.MapCalibration/IMapRegionRefiner.cs`
- Move: `src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs` → `src/Mithril.MapCalibration/MapRegionRefineResult.cs`
- Move: `src/Mithril.MapCalibration.Capture/LocateMetrics.cs` → `src/Mithril.MapCalibration/LocateMetrics.cs`

- [ ] **Step 1: Move files**

```bash
git mv src/Mithril.MapCalibration.Capture/IMapRegionRefiner.cs src/Mithril.MapCalibration/IMapRegionRefiner.cs
git mv src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs src/Mithril.MapCalibration/MapRegionRefineResult.cs
git mv src/Mithril.MapCalibration.Capture/LocateMetrics.cs src/Mithril.MapCalibration/LocateMetrics.cs
```

- [ ] **Step 2: Update each file's namespace**

In each of the three files, change:

From:
```csharp
namespace Mithril.MapCalibration.Capture;
```

To:
```csharp
namespace Mithril.MapCalibration;
```

In [MapRegionRefineResult.cs](../../../src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs) the existing `using Mithril.MapCalibration.Detection;` (for `MapRect`) can be deleted — after Task 4, `MapRect` lives in the same namespace `Mithril.MapCalibration` and is implicitly resolved.

- [ ] **Step 3: Build, surface consumers, fix `using` statements**

Consumers to check via LSP:
- `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` — registers `IMapRegionRefiner`.
- `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` — consumes `IMapRegionRefiner`, `MapRegionRefineResult`, `LocateMetrics`.
- `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs` (already references `MapRect`).
- `src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs` (may reference `LocateMetrics` from #1005).
- `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs` (may reference `LocateMetrics`).
- `src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs` — implements `IMapRegionRefiner`.

Most consumers already `using Mithril.MapCalibration;` for `MapRect` or `AreaCalibration`. The fix is "delete any now-redundant `using` of the old namespace".

- [ ] **Step 4: Build + tests green (stage; commit at end of Phase B)**

Run: `dotnet build Mithril.slnx && dotnet test Mithril.slnx`
Expected: all green.

```bash
git add -A
```

---

### Task 7: Move `ProcessAssetExtractor` to services-tier `Internal/`

**Files:**
- Move: `src/Mithril.MapCalibration/Detection/ProcessAssetExtractor.cs` → `src/Mithril.MapCalibration/Internal/ProcessAssetExtractor.cs`

- [ ] **Step 1: Move file**

```bash
git mv src/Mithril.MapCalibration/Detection/ProcessAssetExtractor.cs src/Mithril.MapCalibration/Internal/ProcessAssetExtractor.cs
```

- [ ] **Step 2: Update namespace**

In the moved file, change:

From:
```csharp
namespace Mithril.MapCalibration.Detection;
```

To:
```csharp
namespace Mithril.MapCalibration.Internal;
```

Keep the class `public` — `.Capture`'s DI extension constructs it directly (`new ProcessAssetExtractor(...)` at [CaptureServiceCollectionExtensions.cs:185-188](../../../src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs)).

- [ ] **Step 3: Update `.Capture`'s DI extension**

In `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`:

- The existing `using Mithril.MapCalibration.Detection;` (line 7) was the path to `ProcessAssetExtractor`; after this move the class lives in `Mithril.MapCalibration.Internal`. Add (or use a qualified reference):

```csharp
using Mithril.MapCalibration.Internal;
```

If other types from `Mithril.MapCalibration.Detection` are also referenced from this file (they will be until Task 9 lifts them away), keep both `using` statements for now.

- [ ] **Step 4: Build + tests green (stage; commit at end of Phase B)**

Run: `dotnet build Mithril.slnx && dotnet test Mithril.slnx`
Expected: all green.

```bash
git add -A
```

---

### Phase B commit

- [ ] **Verify everything for Phase B is staged**

```bash
git status
```

Expected: many moved files showing as renames (per `git mv`) and updated `using` statements in consumers.

- [ ] **Commit Phase B**

```bash
git commit -m "$(cat <<'EOF'
refactor(map-calibration): promote contracts-tier types out of Detection/ and .Capture (Phase B)

Types promoted to Mithril.MapCalibration root (the contracts tier):
- Domain types: MapRect, LandmarkReference, CandidateTransform, CanonicalLandmarkTypes
- Boundary interfaces: ICalibrationDetector, ICalibrationConfidenceGate,
  IBaseTextureProvider, IIconTemplateProvider, IAssetExtractor
- Refiner contracts: IMapRegionRefiner, MapRegionRefineResult, LocateMetrics

Service-tier relocation:
- ProcessAssetExtractor moves from Detection/ to Mithril.MapCalibration/Internal/

Spec: docs/planning/map-calibration-detection-project-split/spec.md

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase C — Lift detection algorithms + introduce Detection DI extension (Tasks 8–10)

> **Important:** the build will be RED between Task 8 and Task 10. Tasks 8 and 9 leave the DI graph half-moved (file relocations only); Task 10 introduces the new DI extension and slims the old ones, restoring a green build. Don't run `dotnet build` mid-phase expecting success — only at the Phase C commit gate. This is normal for a sequenced refactor with a single phase-level commit.

### Task 8: Lift `src/Mithril.MapCalibration/Detection/*` to `src/Mithril.MapCalibration.Detection/`

After Task 7, `src/Mithril.MapCalibration/Detection/` contains:
- Algorithm files: `NccTemplateMatch.cs`, `LocalNccDeviation.cs`, `ImageOps.cs`, `DeviationBlobDetector.cs`, `DeviationFloodRimMask.cs`, `BorderMask.cs`, `IconLikelihoodField.cs`, `IconRenderScaler.cs`, `LocalRefine.cs`, `JEvaluator.cs`, `MapCalibrationSolveEngine.cs`, `TypeAwareRansacSolver.cs`, `WholeImageTemplateDetector.cs`, `DeviationBlobCalibrationDetector.cs`, `CalibrationConfidenceGate.cs`
- Types: `GrayImage.cs`, `IconTemplate.cs`, `IconTemplateSet.cs`, `IconTemplateCache.cs`, `RimMaskMode.cs`, `TypedDetection.cs`
- `Internal/CachedBaseTextureProvider.cs`, `Internal/CachedIconTemplateProvider.cs`, `Internal/BundledIconTemplateLoader.cs`, `Internal/CanonicalAssetHashGate.cs`, `Internal/CanonicalAssetHashes.cs`, `Internal/IconTemplateManifest.cs`, `Internal/MapTextureManifest.cs`, `Internal/SidecarResult.cs`

(plus `Detection` struct lives inside `GrayImage.cs`, see [GrayImage.cs:37](../../../src/Mithril.MapCalibration/Detection/GrayImage.cs))

All these lift wholesale into `src/Mithril.MapCalibration.Detection/`.

**Files:**
- Move: `src/Mithril.MapCalibration/Detection/**/*.cs` → `src/Mithril.MapCalibration.Detection/**/*.cs` (preserve relative structure including `Internal/`)
- Delete: empty `src/Mithril.MapCalibration/Detection/` folder

- [ ] **Step 1: Move the folder wholesale via `git mv`**

```bash
git mv src/Mithril.MapCalibration/Detection src/Mithril.MapCalibration.Detection/AlgorithmFiles
```

(Temporary intermediate name — we'll flatten it in step 2 because the new project's root already corresponds to the `Detection` concept.)

- [ ] **Step 2: Flatten the moved folder so files land at project root**

Move each `.cs` file from `src/Mithril.MapCalibration.Detection/AlgorithmFiles/` to `src/Mithril.MapCalibration.Detection/` (and `AlgorithmFiles/Internal/*.cs` to `Internal/`):

```bash
git mv src/Mithril.MapCalibration.Detection/AlgorithmFiles/*.cs src/Mithril.MapCalibration.Detection/
mkdir -p src/Mithril.MapCalibration.Detection/Internal
git mv src/Mithril.MapCalibration.Detection/AlgorithmFiles/Internal/*.cs src/Mithril.MapCalibration.Detection/Internal/
rmdir src/Mithril.MapCalibration.Detection/AlgorithmFiles/Internal
rmdir src/Mithril.MapCalibration.Detection/AlgorithmFiles
```

(On PowerShell: use `Move-Item`; the `git mv` rename detection treats `Move-Item` results identically as long as the file content is unchanged.)

- [ ] **Step 3: Namespaces are already correct**

Every lifted file's namespace was already `Mithril.MapCalibration.Detection` (top-level for algorithms, `.Internal` for cache layer). The project change doesn't change the namespace — those files already used the namespace that matches the new project's root namespace. No `namespace` edits needed inside the lifted files.

- [ ] **Step 4: Stage (build will be RED — fixed in Task 10)**

The `MapCalibrationServiceCollectionExtensions` in `Mithril.MapCalibration` still references the lifted types via `using Mithril.MapCalibration.Detection;`, which no longer resolves (those types are now in a project the contracts tier doesn't reference). The intentional red state ends at Task 10.

```bash
git add -A
```

---

### Task 9: Lift `FeatureMatchingRefiner` + ORB cache from `.Capture` to `.Detection`

**Files:**
- Move: `src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs` → `src/Mithril.MapCalibration.Detection/FeatureMatchingRefiner.cs`
- Move: `src/Mithril.MapCalibration.Capture/Internal/CachedOrbDescriptorProvider.cs` → `src/Mithril.MapCalibration.Detection/Internal/CachedOrbDescriptorProvider.cs`
- Move: `src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorWriter.cs` → `src/Mithril.MapCalibration.Detection/Internal/OrbDescriptorWriter.cs`
- Move: `src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorManifest.cs` → `src/Mithril.MapCalibration.Detection/Internal/OrbDescriptorManifest.cs`

- [ ] **Step 1: Move files**

```bash
git mv src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs src/Mithril.MapCalibration.Detection/FeatureMatchingRefiner.cs
git mv src/Mithril.MapCalibration.Capture/Internal/CachedOrbDescriptorProvider.cs src/Mithril.MapCalibration.Detection/Internal/CachedOrbDescriptorProvider.cs
git mv src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorWriter.cs src/Mithril.MapCalibration.Detection/Internal/OrbDescriptorWriter.cs
git mv src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorManifest.cs src/Mithril.MapCalibration.Detection/Internal/OrbDescriptorManifest.cs
```

- [ ] **Step 2: Update each file's namespace**

In all four files, change:

From:
```csharp
namespace Mithril.MapCalibration.Capture;          // FeatureMatchingRefiner
namespace Mithril.MapCalibration.Capture.Internal; // CachedOrbDescriptorProvider, OrbDescriptorWriter, OrbDescriptorManifest
```

To:
```csharp
namespace Mithril.MapCalibration.Detection;          // FeatureMatchingRefiner
namespace Mithril.MapCalibration.Detection.Internal; // CachedOrbDescriptorProvider, OrbDescriptorWriter, OrbDescriptorManifest
```

- [ ] **Step 3: Stage (build will be RED — fixed in Task 10)**

`CaptureServiceCollectionExtensions.cs` still references `FeatureMatchingRefiner`, `Internal.CachedOrbDescriptorProvider`, `Internal.OrbDescriptorWriter` from their old locations. Don't fix those references here — Task 10 (below) replaces this whole block of DI wiring with a single call to `AddMithrilMapCalibrationDetection()`, so editing it now would just be wasted work.

```bash
git add -A
```

---

### Task 10: Introduce `DetectionServiceCollectionExtensions` and slim consumer DI files

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs`
- Modify: `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`

- [ ] **Step 1: Create the new DI extension**

Write `src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs`:

```csharp
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection.Internal;

namespace Mithril.MapCalibration.Detection.DependencyInjection;

public static class DetectionServiceCollectionExtensions
{
    /// <summary>
    /// Register the headless detect→solve engine: the deviation-blob
    /// <see cref="ICalibrationDetector"/>, the <see cref="ICalibrationConfidenceGate"/>,
    /// the <see cref="MapCalibrationSolveEngine"/>, the <see cref="IIconTemplateProvider"/>
    /// (per-attempt <see cref="CachedIconTemplateProvider"/> over the asset cache dir),
    /// an <see cref="IBaseTextureProvider"/> over the same cache, the
    /// <see cref="IMapRegionRefiner"/> backed by <see cref="FeatureMatchingRefiner"/>,
    /// and its ORB descriptor cache (<see cref="CachedOrbDescriptorProvider"/> +
    /// <see cref="OrbDescriptorWriter"/>). Independent of
    /// <see cref="Mithril.MapCalibration.DependencyInjection.MapCalibrationServiceCollectionExtensions.AddMithrilMapCalibration"/>
    /// (the persistence registration) — register either or both.
    /// </summary>
    public static IServiceCollection AddMithrilMapCalibrationDetection(
        this IServiceCollection services,
        string assetCacheDir,
        string? pgVersion = null)
    {
        if (string.IsNullOrWhiteSpace(assetCacheDir))
            throw new System.ArgumentException("assetCacheDir required", nameof(assetCacheDir));

        services.AddSingleton<IIconTemplateProvider>(sp =>
            new CachedIconTemplateProvider(
                assetCacheDir,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Templates")));
        services.AddSingleton<IBaseTextureProvider>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var gate = CanonicalAssetHashGate.Load(loggerFactory?.CreateLogger("Mithril.MapCalibration.HashGate"));
            return new CachedBaseTextureProvider(
                assetCacheDir,
                gate,
                pgVersion,
                loggerFactory?.CreateLogger("Mithril.MapCalibration.BaseTexture"));
        });
        services.AddSingleton<ICalibrationDetector, DeviationBlobCalibrationDetector>();
        services.AddSingleton<ICalibrationConfidenceGate, CalibrationConfidenceGate>();
        services.TryAddSingleton<MapCalibrationSolverOptions>();
        services.AddSingleton(sp => new MapCalibrationSolveEngine(
            sp.GetRequiredService<ICalibrationDetector>(),
            sp.GetRequiredService<ICalibrationConfidenceGate>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Engine"),
            sp.GetRequiredService<MapCalibrationSolverOptions>()));

        // FeatureMatchingRefiner + ORB descriptor cache. The refiner's internal
        // cache-aware ctor wires the on-disk ORB descriptor reader+writer below; the
        // engine calls FeatureMatchingRefiner.SetAreaKey(area) before each Refine so
        // the cache key is populated (the IMapRegionRefiner interface stays narrow;
        // runtime-cast in AutoCalibrationEngine).
        services.TryAddSingleton<MapCalibrationLocateOptions>();
        services.TryAddSingleton<CachedOrbDescriptorProvider>(sp =>
        {
            var opts = sp.GetRequiredService<MapCalibrationLocateOptions>();
            return new CachedOrbDescriptorProvider(
                cacheDir: assetCacheDir,
                orbParamsHash: ComputeOrbParamsHash(opts),
                logger: sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.OrbCache"));
        });
        services.TryAddSingleton<OrbDescriptorWriter>(sp =>
        {
            var opts = sp.GetRequiredService<MapCalibrationLocateOptions>();
            return new OrbDescriptorWriter(
                cacheDir: assetCacheDir,
                orbParamsHash: ComputeOrbParamsHash(opts),
                logger: sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.OrbCache"));
        });
        services.AddSingleton<IMapRegionRefiner>(sp =>
            new FeatureMatchingRefiner(
                options: sp.GetRequiredService<MapCalibrationLocateOptions>(),
                logger: sp.GetService<ILogger<FeatureMatchingRefiner>>(),
                cachedDescriptors: sp.GetService<CachedOrbDescriptorProvider>(),
                writer: sp.GetService<OrbDescriptorWriter>()));
        return services;
    }

    /// <summary>
    /// Canonical SHA-256 of the locate options that affect the ORB-descriptor
    /// cache identity. Identical formula to the prior CaptureServiceCollectionExtensions
    /// implementation (PR-2 of #1009); preserved verbatim so existing on-disk caches
    /// keyed by this hash stay valid across the project split.
    /// </summary>
    private static string ComputeOrbParamsHash(MapCalibrationLocateOptions opts)
    {
        var s = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"orb-v1|nFeatures={opts.OrbNFeatures}|loweRatio={opts.LoweRatio:F4}|ransacPx={opts.RansacReprojectionThresholdPx:F4}");
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        return System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
```

- [ ] **Step 2: Slim `CaptureServiceCollectionExtensions.cs`**

In `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`:

1. Add `using Mithril.MapCalibration.Detection.DependencyInjection;` at the top.
2. Replace the `services.AddMithrilMapCalibrationEngine(...)` call at line 71 with:

```csharp
        // Detection tier (Phase-1 detect→solve engine + #931 cache providers +
        // FeatureMatchingRefiner + ORB descriptor cache) — spec: detection project split.
        services.AddMithrilMapCalibrationDetection(assetCacheDir, pgVersion);
```

3. Delete the now-orphan blocks that registered detection / refiner types in `.Capture`:
   - `services.AddSingleton<ICalibrationConfidenceGate>(sp => BuildConfidenceGate(...));` at line 76-77 — KEEP. The override is capture-specific (it wires `GameConfig.CalibrationGoodResidualPx`), and after the moved registrations from `AddMithrilMapCalibrationDetection`, this last-registration-wins override stays correct.
   - `services.AddSingleton<IMapRegionRefiner>(sp => new FeatureMatchingRefiner(...));` at lines 105-110 — DELETE. Now in `AddMithrilMapCalibrationDetection`.
   - `services.TryAddSingleton<MapCalibrationLocateOptions>();` at line 124 — DELETE. Now in `AddMithrilMapCalibrationDetection`.
   - `services.TryAddSingleton<Internal.CachedOrbDescriptorProvider>(...)` at lines 126-133 — DELETE.
   - `services.TryAddSingleton<Internal.OrbDescriptorWriter>(...)` at lines 135-142 — DELETE.
4. Delete the `ComputeOrbParamsHash` private helper at lines 279-286 — moved to `DetectionServiceCollectionExtensions`.
5. Delete the unused `using Mithril.MapCalibration.Detection;` if no remaining code in the file references that namespace.

- [ ] **Step 3: Slim `MapCalibrationServiceCollectionExtensions.cs`**

In `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`:

1. Delete the `AddMithrilMapCalibrationEngine` method (lines 64-121).
2. Delete now-orphan `using` statements: `using Mithril.MapCalibration.Detection;` (line 5), `using Mithril.MapCalibration.Detection.Internal;` (line 6).
3. The class XML doc header note "Register `IMapCalibrationService` ..." stays — that's still what `AddMithrilMapCalibration` does.

- [ ] **Step 4: Build + tests green (Phase C now returns to green)**

Run: `dotnet build Mithril.slnx && dotnet test Mithril.slnx`
Expected: all green. Watch in particular for capture-tier DI integration tests (e.g. `tests/Mithril.MapCalibration.Capture.Tests/` test classes that instantiate the DI graph end-to-end) — these need to see `IMapRegionRefiner` and friends resolve, which they will because `AddMithrilMapCalibrationCapture` calls `AddMithrilMapCalibrationDetection`.

```bash
git add -A
```

---

### Phase C commit

- [ ] **Verify everything for Phase C is staged**

```bash
git status
```

Expected: many file moves (`git mv` rename detection), plus the new `DetectionServiceCollectionExtensions.cs`, plus edits to the two DI extension files in `.MapCalibration` and `.Capture`.

- [ ] **Commit Phase C**

```bash
git commit -m "$(cat <<'EOF'
refactor(map-calibration): lift detection algorithms + introduce Detection DI extension (Phase C)

- Lift src/Mithril.MapCalibration/Detection/* into src/Mithril.MapCalibration.Detection/
- Lift FeatureMatchingRefiner + ORB descriptor cache (CachedOrbDescriptorProvider,
  OrbDescriptorWriter, OrbDescriptorManifest) from .Capture into .Detection
- Introduce AddMithrilMapCalibrationDetection in
  Mithril.MapCalibration.Detection.DependencyInjection, owning the OpenCv-tied
  registrations (IMapRegionRefiner / FeatureMatchingRefiner,
  MapCalibrationLocateOptions, CachedOrbDescriptorProvider, OrbDescriptorWriter)
  plus the ComputeOrbParamsHash helper
- Slim CaptureServiceCollectionExtensions: replaces AddMithrilMapCalibrationEngine
  call with AddMithrilMapCalibrationDetection; deletes the orphaned refiner/ORB
  registrations now owned by .Detection; keeps the GameConfig-wired
  ICalibrationConfidenceGate override
- Delete AddMithrilMapCalibrationEngine from MapCalibrationServiceCollectionExtensions
  (replaced by .Detection's extension)

Spec: docs/planning/map-calibration-detection-project-split/spec.md

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase D — Tighten OpenCv boundary + finalize prose (Tasks 11–12)

### Task 11: Remove OpenCv reference from `.Capture`; tighten arch-test allowlist

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj`
- Modify: `tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs`

- [ ] **Step 1: Remove OpenCv PackageReference from `.Capture.csproj`**

In `src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj`, delete the OpenCv block. Before:

```xml
    <!-- #978: sub-pixel screenshot↔texture ECC registration in-process. Sanctioned
         in-process exception to the #921 decoder-free split (allowlisted for THIS
         assembly only by ShippedGraphDecoderFreeTests). OpenCvSharp is an alignment
         library here (FindTransformECC), not an asset decoder. -->
    <PackageReference Include="OpenCvSharp4" />
    <PackageReference Include="OpenCvSharp4.runtime.win" />
```

After: those four lines deleted entirely. (The OpenCv-using code lives in `.Detection` after Phase C; `.Capture` no longer needs the dep directly.)

- [ ] **Step 2: Remove `.Capture` from arch-test allowlist**

In `tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs`, replace:

```csharp
    private static readonly Dictionary<string, string[]> PackageAllowlistByProject = new(StringComparer.OrdinalIgnoreCase)
    {
        // Transitional state — `.Capture` keeps its entry until the OpenCv-using code
        // is lifted into `.Detection` (this plan, Task 10). The final state has only
        // `.Detection`.
        ["Mithril.MapCalibration.Capture.csproj"] = ["OpenCvSharp"],
        ["Mithril.MapCalibration.Detection.csproj"] = ["OpenCvSharp"],
    };
```

with:

```csharp
    private static readonly Dictionary<string, string[]> PackageAllowlistByProject = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mithril.MapCalibration.Detection.csproj"] = ["OpenCvSharp"],
    };
```

- [ ] **Step 3: Build + tests green (stage; commit at end of Phase D)**

Run: `dotnet build Mithril.slnx && dotnet test Mithril.slnx`
Expected: all green. `ShippedGraphDecoderFreeTests` now strictly enforces that only `.Detection` carries OpenCv.

```bash
git add src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs
```

---

### Task 12: Rewrite stale prose; verification

**Files:**
- Modify: `tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs` (class-level XML doc)
- Modify: `src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj` (header comment, if any remained)

- [ ] **Step 1: Rewrite arch-test class XML doc**

In `tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs`, lines 34-44 currently read:

```csharp
    /// <para><b>Sanctioned in-process OpenCv exception (issue #978):</b> screenshot↔
    /// texture registration (<c>Cv2.FindTransformECC</c>) runs IN-PROCESS in the
    /// calibration capture assembly (<c>Mithril.MapCalibration.Capture</c>). Maintainer
    /// decision: OpenCvSharp is an <i>alignment</i> library, not an asset decoder; Mithril
    /// is Windows-only WPF (not trimmed / not AOT) and registration is occasional, so an
    /// in-process call beats an out-of-process sidecar round-trip and its native-runtime
    /// staging cost. To keep that exception EXPLICIT and stop OpenCv silently spreading,
    /// <c>OpenCvSharp</c> is added to <see cref="ForbiddenPackages"/> across <c>src/**</c>
    /// and re-permitted ONLY for the named project in
    /// <see cref="PackageAllowlistByProject"/> — the #921 decoder-free split is relaxed via
    /// a named allowlist, never removed and never replaced with a sidecar.</para>
```

Replace with:

```csharp
    /// <para><b>Sanctioned in-process OpenCv exception (issue #978, spec
    /// map-calibration-detection-project-split):</b> screenshot↔texture feature-matching
    /// locate (ORB + RANSAC via <see cref="FeatureMatchingRefiner"/>) runs IN-PROCESS in
    /// the dedicated calibration detection assembly
    /// (<c>Mithril.MapCalibration.Detection</c>). Maintainer decision: OpenCvSharp is an
    /// <i>alignment</i> library, not an asset decoder; Mithril is Windows-only WPF (not
    /// trimmed / not AOT) and detection runs per-attempt at user-perceived latency, so an
    /// in-process call beats an out-of-process sidecar round-trip and its native-runtime
    /// staging cost. The detection project is the SINGLE named OpenCv home; the capture
    /// project is OpenCv-free (Win32 / WPF capture only). To keep that exception EXPLICIT
    /// and stop OpenCv silently spreading, <c>OpenCvSharp</c> is in
    /// <see cref="ForbiddenPackages"/> across <c>src/**</c> and re-permitted ONLY for the
    /// named project in <see cref="PackageAllowlistByProject"/>. The #921 decoder-free
    /// split is relaxed via that named allowlist, never removed and never replaced with a
    /// sidecar.</para>
```

(Key changes: `FindTransformECC` → `FeatureMatchingRefiner` / ORB + RANSAC; `Mithril.MapCalibration.Capture` → `Mithril.MapCalibration.Detection`; "registration is occasional" → "detection runs per-attempt at user-perceived latency"; spec reference added.)

- [ ] **Step 2: Verify `FindTransformECC` is gone from `src/**`**

Run: `Grep` (or `rg`) for `FindTransformECC` in `src/`. Expected: zero matches.

If any remain (e.g. in a stale comment), rewrite the prose to drop the reference.

- [ ] **Step 3: Final build + tests green (stage; commit at end of Phase D)**

Run: `dotnet build Mithril.slnx && dotnet test Mithril.slnx`
Expected: all green.

```bash
git add tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs
```

---

### Phase D commit

- [ ] **Verify everything for Phase D is staged**

```bash
git status
```

Expected: `.Capture.csproj` (OpenCv removed) + `ShippedGraphDecoderFreeTests.cs` (allowlist tightened + class-level XML doc rewritten).

- [ ] **Commit Phase D**

```bash
git commit -m "$(cat <<'EOF'
chore(arch-test): tighten OpenCv allowlist to .Detection only + rewrite #978 prose (Phase D)

- Remove OpenCv PackageReference from Mithril.MapCalibration.Capture.csproj
- Drop .Capture entry from ShippedGraphDecoderFreeTests.PackageAllowlistByProject;
  allowlist now strictly names .Detection only — any other src/** project taking
  an OpenCvSharp reference is a violation
- Rewrite the class-level XML doc to:
  - name FeatureMatchingRefiner (ORB+RANSAC) as the current OpenCv consumer
  - drop the stale FindTransformECC reference (deleted in PR-4 of #1009)
  - cite Mithril.MapCalibration.Detection as the single OpenCv home
  - cite spec map-calibration-detection-project-split

Spec: docs/planning/map-calibration-detection-project-split/spec.md

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase E — Finalize (Task 13)

### Task 13: Flip INDEX.md status, file the follow-up issue (optional), open PR

**Files:**
- Modify: `docs/planning/INDEX.md`

- [ ] **Step 1: Flip status from `active` to `shipped`**

(This step lands once the implementation is fully verified and the PR is ready to merge — NOT during implementation. It's listed here so the engineer remembers.)

In `docs/planning/INDEX.md`, find the row:

```markdown
| [map-calibration-detection-project-split](map-calibration-detection-project-split/) | active | _no issue_ | Extract `Mithril.MapCalibration.Detection` project; OpenCv allowlist moves there, `.Capture` re-becomes Win32-only |
```

Update to (filling in the actual PR number once created):

```markdown
| [map-calibration-detection-project-split](map-calibration-detection-project-split/) | shipped | [#NNNN](https://github.com/moumantai-gg/mithril/pull/NNNN) | Extract `Mithril.MapCalibration.Detection` project; OpenCv allowlist moves there, `.Capture` re-becomes Win32-only |
```

- [ ] **Step 2: (Optional) File the follow-up issue**

The spec's [§Follow-up](spec.md#follow-up) captures the OpenCv-vs-template-matcher migration scope + harness shape. File as a separate GitHub issue with that section as the body; link back to this spec. The spec's "Tracked in: _issue to be filed; placeholder_" header can be updated to reference this PR's number once opened, and the follow-up issue if filed.

- [ ] **Step 3: Open PR**

Per CLAUDE.md the branch policy blocks direct commits to main; create a PR:

```bash
gh pr create --title "refactor(map-calibration): extract .Detection project (spec: detection-project-split)" --body "$(cat <<'EOF'
## Summary

Pure refactor extracting `Mithril.MapCalibration.Detection` as its own project. See [docs/planning/map-calibration-detection-project-split/spec.md](docs/planning/map-calibration-detection-project-split/spec.md) for full rationale and [plan.md](docs/planning/map-calibration-detection-project-split/plan.md) for the task breakdown.

Four phase commits in this PR, intended to remain as distinct commits on the PR branch and squash to one on `main` at merge time:

1. **Phase A** — `refactor(map-calibration): add .Detection project skeleton`
   Empty project, slnx entry, arch-test allowlist (transitional), ProjectReferences from `.Capture` / Shell / capture-tests.
2. **Phase B** — `refactor(map-calibration): promote contracts-tier types out of Detection/ and .Capture`
   Move `MapRect`, `LandmarkReference`, `CandidateTransform`, `CanonicalLandmarkTypes`, boundary interfaces (`ICalibrationDetector`, `ICalibrationConfidenceGate`, `IBaseTextureProvider`, `IIconTemplateProvider`, `IAssetExtractor`), refiner contracts (`IMapRegionRefiner`, `MapRegionRefineResult`, `LocateMetrics`), and relocate `ProcessAssetExtractor` to services-tier `Internal/`.
3. **Phase C** — `refactor(map-calibration): lift detection algorithms + introduce Detection DI extension`
   Lift `src/Mithril.MapCalibration/Detection/*` and `FeatureMatchingRefiner` + ORB descriptor cache into `.Detection`. Introduce `AddMithrilMapCalibrationDetection`; slim `MapCalibrationServiceCollectionExtensions` and `CaptureServiceCollectionExtensions`.
4. **Phase D** — `chore(arch-test): tighten OpenCv allowlist to .Detection only + rewrite #978 prose`
   Remove OpenCv from `.Capture.csproj`, drop `.Capture` from `ShippedGraphDecoderFreeTests.PackageAllowlistByProject`, rewrite class-level XML doc (no more `FindTransformECC`; names `.Detection` as the single OpenCv home).

No algorithm changes anywhere — every `.cs` file move is a `git mv` rename, picked up by GitHub's rename-detection so the diff reviews as renames + small `using`/namespace edits, not as add+delete.

## Test plan

- [x] `dotnet build Mithril.slnx` green
- [x] `dotnet test Mithril.slnx` green (all suites including `ShippedGraphDecoderFreeTests`)
- [x] `grep -r "FindTransformECC" src/` empty
- [ ] Launch Mithril once + open Legolas, trigger auto-calibration via hotkey → confirm the OpenCv-backed refiner still runs (smoke check; arch test confirms structurally but a runtime touch is cheap)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-review checklist (engineer)

Before opening the PR, walk through:

- [ ] **Build green** on `Mithril.slnx` from clean state (delete `bin/`, `obj/`, re-build).
- [ ] **All tests green** on `Mithril.slnx`. In particular: every test under `tests/Mithril.MapCalibration.Tests/`, `tests/Mithril.MapCalibration.Capture.Tests/`, and `tests/Mithril.Shared.Tests/Architecture/`.
- [ ] **No `FindTransformECC` in src/.** `grep -r FindTransformECC src/` → empty.
- [ ] **`.Capture.csproj` has no OpenCv reference.** `grep -i OpenCv src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj` → empty.
- [ ] **`.Detection.csproj` has OpenCv reference.** `grep -i OpenCv src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj` → two `PackageReference` lines.
- [ ] **`Mithril.MapCalibration/Detection/` is empty / deleted.** `ls src/Mithril.MapCalibration/Detection/ 2>&1` → "no such file or directory".
- [ ] **Runtime smoke test.** Launch the shell, exercise the auto-calibration hotkey once. The OpenCv-backed `FeatureMatchingRefiner` running confirms the DI split + transitive native-runtime loading both work end-to-end. Memory: `consumerless_service_verify_via_diagnostics`, `di_cycle_invisible_to_unit_tests`.
- [ ] **Spec coverage:** every section of `spec.md` has a corresponding task in this plan that implements (or explicitly defers / declares out-of-scope) it.
