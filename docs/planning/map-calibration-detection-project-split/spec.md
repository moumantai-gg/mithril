# Map-calibration: extract detection into its own project

**Tracked in:** [#1028](https://github.com/moumantai-gg/mithril/pull/1028) (implementation). Follow-up work tracked in [#1030](https://github.com/moumantai-gg/mithril/issues/1030).

## Context

### What the split is for

`Mithril.MapCalibration.Capture` currently holds two distinct kinds of code:

1. **Win32 / WPF capture concerns:** BitBlt screen capture, `Win32GameWindowLocator`, overlay blank, bbox draw, hotkeys, the orchestrator (`AutoCalibrationEngine`), filesystem diagnostic-bundle sinks.
2. **OpenCv-using detection concerns:** [`FeatureMatchingRefiner`](../../../src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs) (ORB + RANSAC locate) plus its descriptor cache helpers ([`CachedOrbDescriptorProvider`](../../../src/Mithril.MapCalibration.Capture/Internal/CachedOrbDescriptorProvider.cs), [`OrbDescriptorWriter`](../../../src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorWriter.cs), [`OrbDescriptorManifest`](../../../src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorManifest.cs)).

The second kind only lives in `.Capture` because that's where the `OpenCvSharp` allowlist entry happens to be — a residue of issue [#978](https://github.com/moumantai-gg/mithril/issues/978), which carved a narrow in-process exception to the [#921](https://github.com/moumantai-gg/mithril/issues/921) decoder-free split for the (now-removed) ECC refine. The exception's location stopped fitting its meaning once ECC was deleted in PR-4 of [#1009](https://github.com/moumantai-gg/mithril/issues/1009) (`4f298446`) and ORB+RANSAC became the canonical refine.

In parallel, the BCL-only `src/Mithril.MapCalibration/Detection/*` folder (~3000 LOC of hand-rolled NCC / blob / morphology / scaler / RANSAC / solve-engine code) is its own conceptual unit but currently shares a project with the contracts/services tier.

### What this spec changes

Split detection out of both `Mithril.MapCalibration` and `Mithril.MapCalibration.Capture` into a dedicated `Mithril.MapCalibration.Detection` project:

```
Mithril.MapCalibration              [BCL-only contracts + services tier]
        ▲
        │  (ProjectReference)
        │
Mithril.MapCalibration.Detection    [single named home for ALL detection
                                     algorithms; OpenCvSharp allowlist
                                     entry moves here; no Win32 / WPF deps]
        ▲
        │  (ProjectReference)
        │
Mithril.MapCalibration.Capture      [Win32 + WPF capture only; loses its
                                     OpenCvSharp reference]
```

### What this spec does NOT change

- **No algorithm changes.** This is a pure refactor: file moves, namespace adjustments, csproj edits. Every existing detection algorithm keeps its current implementation.
- **No OpenCv migration of NCC / blob / morphology / scaler / RANSAC.** That work is scoped to a separate follow-up (see [§Follow-up](#follow-up)).
- **No new harness or experiments.** Existing `SynthesisProbe` E1–E5 keep working as-is.
- **No asset-extractor sidecar changes.** [`ProcessAssetExtractor`](../../../src/Mithril.MapCalibration/Detection/ProcessAssetExtractor.cs) moves location per the type-placement rule below, but the process boundary to `tools/Mithril.AssetExtractor` stays exactly as today (`Process`-only link enforced by [`ShippedGraphDecoderFreeTests`](../../../tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs)).

## Architecture

### Project layering (target state)

| Project | Responsibility | Allowed deps beyond BCL |
|---|---|---|
| `Mithril.MapCalibration` | Domain model types, service interfaces, service-tier impls (`MapCalibrationService`, `UserRefinementStore`, `BundledBaselineLoader`, `ProcessAssetExtractor`), options, JSON contexts | None |
| `Mithril.MapCalibration.Detection` | Detection algorithms (NCC, local NCC deviation, image ops, blob detector, morphology, flood masks, scaler, RANSAC, J-evaluator, solve engine, local refine, ORB feature-matching refiner, ORB descriptor cache) | **OpenCvSharp4** + **OpenCvSharp4.runtime.win** (named allowlist) |
| `Mithril.MapCalibration.Capture` | Win32 screen capture, window locator, captured-frame model, WPF overlay, bbox draw, snip math, hotkeys, AutoCalibrationEngine + AutoCalibrationTrigger, diagnostics bundle sinks | `Mithril.MapCalibration.Detection` |

Dependency direction: `.Capture → .Detection → .MapCalibration`. No cycles.

### Why a project per layer, not interfaces per op or named-allowlist widening

This is the same outcome we'd get by widening the existing #978 named allowlist to include `Mithril.MapCalibration.csproj`, but expressed as a project-boundary rather than a same-project relaxation. Reasons:

1. **Conceptual fit.** Detection is conceptually its own subsystem; promoting it to a project surfaces the boundary in the dependency graph rather than burying it in a textual allowlist.
2. **Capture re-becomes capture.** Removing OpenCv from `.Capture` realigns the project name with what it does (screen capture and Win32/WPF orchestration).
3. **Smaller OpenCv blast radius than allowlist widening.** OpenCv lives in *one* project named for what it does, instead of two named for unrelated reasons.
4. **Per-interface-op alternatives rejected.** A `Mithril.MapCalibration.Cv` skeleton with an interface per op was considered; the plumbing cost dominates for ops with stable, well-known shapes (resize, NCC, flood fill) that nobody is going to mock.

## Type placement

### Rule

A type moves up to the contracts tier (`Mithril.MapCalibration` root) iff it is referenced from outside `Mithril.MapCalibration.Detection` — by `.Capture`, by the services tier in `Mithril.MapCalibration/Internal/`, by modules, or by tests. Types only used inside detection algorithms stay in `.Detection`.

For boundary interfaces specifically (e.g. `ICalibrationDetector`, `IBaseTextureProvider`): interface in contracts (it's the service contract), impl in `.Detection` (it's the machinery). Caching impls of contract interfaces can live in either tier; default is `.Detection/Internal/` (closer to the data they cache).

### Concrete moves out of `Detection/` into contracts (`Mithril.MapCalibration` root)

| Type | Current location | Why contract |
|---|---|---|
| `MapRect` | [Detection/MapRect.cs](../../../src/Mithril.MapCalibration/Detection/MapRect.cs) | Referenced by `.Capture/AutoCalibrationEngine`, `.Capture/FeatureMatchingRefiner`, `.Capture/Diagnostics/CalibrationBundleJson`, services-tier `MapCalibrationJsonContext` |
| `LandmarkReference` | [Detection/LandmarkReference.cs](../../../src/Mithril.MapCalibration/Detection/LandmarkReference.cs) | Referenced by `.Capture/IAreaReferenceProvider`, `ReferenceDataAreaReferenceProvider` |
| `CandidateTransform` | [Detection/CandidateTransform.cs](../../../src/Mithril.MapCalibration/Detection/CandidateTransform.cs) | Referenced by `.Capture/IMapCalibrationSolver`, `.Capture/AutoCalibrationEngine` |
| `CanonicalLandmarkTypes` | [Detection/CanonicalLandmarkTypes.cs](../../../src/Mithril.MapCalibration/Detection/CanonicalLandmarkTypes.cs) | Domain vocabulary; referenced beyond `.Detection` |
| `ICalibrationDetector`, `ICalibrationConfidenceGate` | [Detection/I*.cs](../../../src/Mithril.MapCalibration/Detection/) | Boundary interfaces wired by `MapCalibrationServiceCollectionExtensions` |
| `IBaseTextureProvider`, `IIconTemplateProvider`, `IAssetExtractor` | [Detection/I*.cs](../../../src/Mithril.MapCalibration/Detection/) | DI-registered service interfaces with consumers across tiers |

### Concrete moves out of `.Capture` into contracts

| Type | Current location | Why contract |
|---|---|---|
| `IMapRegionRefiner` | [src/Mithril.MapCalibration.Capture/IMapRegionRefiner.cs](../../../src/Mithril.MapCalibration.Capture/IMapRegionRefiner.cs) | Refiner impl moves to `.Detection`; interface needs to live at or above where `.Capture` and `.Detection` both reach it |
| `MapRegionRefineResult` | [src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs](../../../src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs) | Public output of the refiner; same reasoning |
| `LocateMetrics` | [src/Mithril.MapCalibration.Capture/LocateMetrics.cs](../../../src/Mithril.MapCalibration.Capture/LocateMetrics.cs) | Held by `MapRegionRefineResult.Metrics` (added in [#1005](https://github.com/moumantai-gg/mithril/issues/1005) / [#1023](https://github.com/moumantai-gg/mithril/pull/1023)); must travel with the result type into contracts |

### Stays in `.Detection`

All `Detection/*.cs` algorithm files: `NccTemplateMatch`, `LocalNccDeviation`, `ImageOps`, `DeviationBlobDetector` (+ internals `Morphology`, `ConnectedComponents`, `BlobFeat`, `BlobClass`, `BlobOptions`), `DeviationFloodRimMask`, `BorderMask`, `IconLikelihoodField`, `IconRenderScaler`, `LocalRefine`, `JEvaluator`, `MapCalibrationSolveEngine`, `TypeAwareRansacSolver`, `WholeImageTemplateDetector`, `DeviationBlobCalibrationDetector`, `DeviationBlobDetector.Classify` + supporting structs, `TypedDetection`, `CalibrationConfidenceGate` (impl), `RimMaskMode`, `Detection` (struct), `GrayImage`, `IconTemplate`, `IconTemplateSet`, `IconTemplateCache`.

### Lifts from `.Capture` into `.Detection`

Wholesale, no internal changes:

- [FeatureMatchingRefiner.cs](../../../src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs)
- [Internal/CachedOrbDescriptorProvider.cs](../../../src/Mithril.MapCalibration.Capture/Internal/CachedOrbDescriptorProvider.cs)
- [Internal/OrbDescriptorWriter.cs](../../../src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorWriter.cs)
- [Internal/OrbDescriptorManifest.cs](../../../src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorManifest.cs)

### Edge-case placement (resolved during impl)

| Type | Currently | Target | Why |
|---|---|---|---|
| `ProcessAssetExtractor` | `Detection/ProcessAssetExtractor.cs` | `Mithril.MapCalibration/Internal/` | It's the sidecar-process bridge for asset extraction — service-tier behaviour, not algorithmic |
| `Internal/CachedBaseTextureProvider`, `Internal/CachedIconTemplateProvider`, `Internal/BundledIconTemplateLoader` | `Detection/Internal/` | `Detection/Internal/` (stays) | Caching impls live next to the data they cache |
| `Internal/MapTextureManifest`, `Internal/IconTemplateManifest`, `Internal/CanonicalAssetHashGate`, `Internal/CanonicalAssetHashes`, `Internal/SidecarResult` | `Detection/Internal/` | `Detection/Internal/` (stays) | Internal to detection's asset-handling |

## Arch-test update

[ShippedGraphDecoderFreeTests](../../../tests/Mithril.Shared.Tests/Architecture/ShippedGraphDecoderFreeTests.cs) carries the named allowlist. After this change:

```csharp
private static readonly Dictionary<string, string[]> PackageAllowlistByProject = new(...)
{
    ["Mithril.MapCalibration.Detection.csproj"] = ["OpenCvSharp"],
};
```

The `Mithril.MapCalibration.Capture.csproj` entry is **removed**.

The class-level XML doc prose comment (currently citing #978 + `FindTransformECC`) is rewritten to:

- Name `Mithril.MapCalibration.Detection` as the single OpenCv home,
- Explain that OpenCv is an alignment-library exception to #921 hosted in the dedicated CV project rather than in `.Capture`,
- Drop the stale `FindTransformECC` reference (replaced by ORB+RANSAC in `FeatureMatchingRefiner`),
- Note that any *other* `src/**` project taking an OpenCvSharp reference remains a violation.

## DI changes

`MapCalibrationServiceCollectionExtensions` (in `.MapCalibration`) keeps registering contracts + services-tier types. New `DetectionServiceCollectionExtensions.AddMapCalibrationDetection(this IServiceCollection)` (in `.Detection`) registers all detection impls (including the refiner and ORB descriptor cache). `CaptureServiceCollectionExtensions.AddMapCalibrationCapture(...)` calls `AddMapCalibrationDetection()` instead of registering refiner/ORB types itself.

## csproj changes

| Project | Before | After |
|---|---|---|
| `Mithril.MapCalibration.csproj` | (BCL-only) | Unchanged |
| `Mithril.MapCalibration.Detection.csproj` | _does not exist_ | New. References `Mithril.MapCalibration`. `PackageReference` to `OpenCvSharp4` + `OpenCvSharp4.runtime.win`. `<UseWPF>` not set. `<AllowUnsafeBlocks>` only if any lifted file needs it (none currently do — `unsafe` is on `.Capture` for `GetDIBits`). `InternalsVisibleTo Include="Mithril.MapCalibration.Detection.Tests"`. |
| `Mithril.MapCalibration.Capture.csproj` | OpenCvSharp + `<UseWPF>` + `<AllowUnsafeBlocks>` | OpenCvSharp **removed**. `<UseWPF>` + `<AllowUnsafeBlocks>` stay (still needed for WIC + GetDIBits). ProjectReference to new `Mithril.MapCalibration.Detection`. Comment header rewritten to drop the stale #978 / ECC note. |
| `Mithril.Shell.csproj` | references `Mithril.MapCalibration` + `.Capture` | Adds explicit `ProjectReference` to `Mithril.MapCalibration.Detection`. The modules-copy build step only stages `*.Module.dll`, so a new `Mithril.*` library that the shell needs at runtime requires an explicit Shell-project reference. |
| `Mithril.MapCalibration.Capture.Tests.csproj` | references `.Capture` | Adds ProjectReference to `Mithril.MapCalibration.Detection` if any tests reach into refiner/ORB types directly |

A new test project `Mithril.MapCalibration.Detection.Tests` may or may not be needed — depends on whether the lifted code has tests in `.Capture.Tests` that should follow it. Resolved during impl.

## Risks + verification

- **Build green** is the primary gate. Pure file-move refactors usually expose two failure modes:
  - Forgotten csproj references (Shell, test projects).
  - Internal types that crossed a project boundary and need an `InternalsVisibleTo` adjustment.
- **Existing tests green** is the secondary gate. The lifted code is unchanged; if a test fails post-split, the cause is plumbing (DI registration order, missing project reference), not behaviour drift.
- **Arch-test red→green transition.** After the allowlist swap, `ShippedGraphDecoderFreeTests.No_src_project_references_a_decoder_or_the_tools_common_lib` should pass. A transient red in mid-PR (during file moves but before allowlist update) is expected.
- **Hidden Detection-internal references from Capture.** `.Capture` may currently reach types under `Detection/` that aren't in the type-placement tables (e.g. via `IconTemplate` if it crossed the boundary). Surfaced by the compiler; resolved either by promotion to contracts or by adding a ProjectReference if the type is genuinely in `.Detection`.
- **Comment-header rot in `.Capture.csproj`** — the existing header citing #978 and `FindTransformECC` is wrong today and must be cleaned up during this change. Verification owed: a grep for "FindTransformECC" in `src/**` should be empty after this lands.

## Follow-up

Tracked in [#1030](https://github.com/moumantai-gg/mithril/issues/1030). The OpenCv migration of detection algorithms is deliberately not part of this spec. Captured from the brainstorm, the follow-up's scope and design hooks are:

- **Scope (i) commodity ops first, (iv) profile-guided thereafter.** Domain math (`TypeAwareRansacSolver`, `JEvaluator`, `IconLikelihoodField`, `MapCalibrationSolveEngine` orchestration, `LocalRefine`) explicitly stays hand-rolled through phase (i).
- **Op-by-op map:** direct one-liners for `ImageOps.{Downsample, Resize, Rotate180, Crop}`, `Morphology.Close`, `ConnectedComponents.Label`, `DeviationFloodRimMask`. Semantic nuance to validate per-bundle for `NccTemplateMatch` (`MatchTemplate` mask only available with `SqDiffNormed` / `CCorrNormed`, not `CCoeffNormed`; sub-pixel parabolic refine stays hand-rolled, fed the OpenCv score field), `LocalNccDeviation` (`Cv2.Integral` + retained branch logic), `BorderMask`, `IconRenderScaler`.
- **Harness shape:** add `E6_OpenCvParity` + `E7_OpenCvPerf` experiments to existing [tools/MapCalibrationFromScreenshot/SynthesisProbe](../../../tools/MapCalibrationFromScreenshot/SynthesisProbe/), reusing `BundleLoader`, `BundleArgsResolver`, `SynthesisProbeWriter`, `SynthesisProbeTracer`. Operator-run, not CI-integrated.
- **Corpus:** developer-curated under `study/bundles/` (gitignored — diagnostic bundles transitively contain PG art and cannot be committed, same principle as "ship canonical hashes, not PG art"). Repo carries a manifest only (bundle IDs + areas + intent labels + collection instructions).
- **Per-op sequencing:** add `OpenCv{Op}` next to `{Op}`, run harness, cutover-and-delete in a follow-up PR. Two-PR pattern (add-only, then cutover-and-delete) avoids the squash-merge problem where an add-then-delete in a single squashed PR is gc-eligible after ~90 days.
- **Open decision deferred to harness data:** whether `Cv2.MatchTemplate(SqDiffNormed, mask)` produces peaks close enough to current `CCoeffNormed`-with-mask. If not, keep `NccTemplateMatch` hand-rolled and migrate only the unmasked-template detectors.

See [#1030](https://github.com/moumantai-gg/mithril/issues/1030) for the working-proposal parity thresholds and per-op PR sequencing.
