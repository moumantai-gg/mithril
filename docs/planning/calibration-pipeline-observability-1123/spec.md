# Calibration detector-pipeline observability — spec

**Issue:** [mithril#1123](https://github.com/moumantai-gg/mithril/issues/1123). **Status:** active. **Branch posture:** docs-only spec/plan PR (`claude/sad-jemison-4abb0e`) against `main`; the implementation lands in a follow-up PR this spec drives.

## 1. Problem

The auto-calibration detector pipeline (`deviation map → foreground threshold → rim mask → morphological close → connected components → blob classification → per-blob template NCC`) has observability ONLY at the outermost orchestrator layer. Every static utility class that owns a real decision branch beneath the orchestrator is silent. The [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) NPC-pip-recall investigation hit this directly: the per-blob/per-template NCC scoring shipped in [mithril#1122](https://github.com/moumantai-gg/mithril/pull/1122) showed healthy template scores but the NPC pip region never produced a blob — and "why didn't this region produce a blob?" lives in code that emits nothing.

The Hogan's Basement bundle (`%LocalAppData%/Mithril/diagnostics/calibration/Map_HogansKeepBasement-20260609-234053-135-accepted/`, 2026-06-09T23:40:53Z) is the worked example. Three visible NPC pips in `06-aligned-screenshot.png` at bboxes `(239,106,16,17)`, `(251,116,17,16)`, `(249,149,16,17)` show as bright blobs in `07-deviation.png`. **23 candidate blobs** detected total — none within ~80 px of any pip. The pip region got eaten between "deviation map" and "blob emerged from `DetectIconBlobs`." The static-utility pipeline (rim mask flood / morph close + 8-connected merge / Classify shape gate) dropped them silently. We can't tell which layer because none emit.

### Why this happened — and why this is a recurring failure mode

[mithril#1093](https://github.com/moumantai-gg/mithril/issues/1093) surveyed at the service-class layer (does [`AutoCalibrationEngine`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs) have an `ILogger`? yes → "well-instrumented"). Static utilities have no `ILogger` field by construction, so they're structurally invisible to that audit. [mithril#1122](https://github.com/moumantai-gg/mithril/pull/1122)'s per-blob scoring inherited the same blind spot — the orchestrator got instrumented; the helpers it calls didn't.

The CLAUDE.md "Instrumentation is not optional" bullet already requires this work in principle; the issue is that the static-utility shape made it easy to skip. User memory [`instrumentation-surveys-include-static-utilities`](file:///C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/instrumentation_surveys_include_static_utilities.md) captures the audit lesson: pure-function helpers need explicit observability seams (callback or instance refactor) before they count as instrumented.

## 2. Goal / scope

In scope — observability seams for the five decision-owning utilities the detector pipeline walks:

| Type | Decision owned | Source |
|---|---|---|
| [`LocalNccDeviation.DeviationMap`](../../../src/Mithril.MapCalibration.Detection/LocalNccDeviation.cs) | per-pixel "added content?" verdict (deviation ≥ threshold) | blob-detection |
| [`DeviationFloodRimMask.Build`](../../../src/Mithril.MapCalibration.Detection/DeviationFloodRimMask.cs) | which pixels get marked as rim (flood from edges through high-deviation paths) | blob-detection AND synthesis-J L_t builder |
| [`Morphology.Close`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs#L160) | which pixels bridge (dilate + erode with configurable radius) | blob-detection |
| [`ConnectedComponents.Label`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs#L115) | which pixels merge into one blob (8-connected) | blob-detection |
| [`DeviationBlobDetector.Classify`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs#L70) | Icon vs Noise vs Fog vs Structure per blob | blob-detection |

Out of scope (called out in §10):

- The actual NPC pip recall fix. This pass is the observability prerequisite. [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) tracks the symptom; the fix lands once this data lets a triager pick the right cause (rim-mask leakage vs sub-pixel locator drift cancelling pip deviation vs `BlobOptions` rejection).
- Whole-image fallback detector ([`WholeImageTemplateDetector`](../../../src/Mithril.MapCalibration.Detection/WholeImageTemplateDetector.cs)). Different path, separate audit.
- Refactoring static utilities to instance classes with `ILogger`. The orchestrator-level seam (D7) is the chosen approach.
- Per-pixel observability inside the static helpers themselves (would shred perf budget).

## 3. Decision ledger

| # | Decision | Reasoning |
|---|---|---|
| D1 | **Observability primitive = stage masks (PNGs) + per-blob records + summary stats.** Detector retains the existing intermediate buffers (`dev`, `fg-initial`, `rimMask`, `fg-after-morph`) when a hook is wired; bundle writes one PNG per stage; one combined JSON carries summary stats + every blob's full features + Classify verdict (Noise/Icon/Fog/Structure). | Brainstorm Q1. The Hogan's failure mode is "which layer ate the pip region (X 220-290, Y 90-170)?" — answered by visual inspection across the stage PNGs. A probe-coord API would require the engine to know pip pixel locations up front, but those are derived only AFTER detection by reading NPC refs back through a candidate calibration that doesn't exist yet at detect time. Summary-stats-only can answer "how many pixels survived rim-mask" but not "did pixel (247, 114) survive." Preserves the [mithril#1122](https://github.com/moumantai-gg/mithril/pull/1122) principle that surfaces ALL observations and lets downstream triage decide what to filter (distinguishing "0.78 below floor" from "0.30 below floor"). |
| D2 | **Sink surface = one aggregate `DetectionDiagnosticHooks? Diagnostics { get; init; }` field on `DetectionRequest`.** The hooks record carries four `Action<T>?` fields, each independently null-able. The existing [mithril#1121](https://github.com/moumantai-gg/mithril/issues/1121) `BlobScoreSink` stays as a sibling field rather than joining the aggregate (preserves the shipped contract; sibling is one field of clutter, not four). | Brainstorm Q2. One aggregate keeps the related sinks together at the call site (one null check, one `with` clause to wire), preserves the "future readers see the surface in one place" property, and matches the layering pattern other Mithril diagnostic types use (`SynthesisDiagnostics` carries multiple co-emitted fields under one record). |
| D3 | **Bundle dump = one JSON file (`10c-blob-pipeline.json`) + per-stage PNGs.** JSON has top-level sections `deviation`, `rimMasks[]`, `morph`, `blobs[]` under a single `schemaVersion`. PNGs are per (stage, orientation, pipeline) tuple. | Brainstorm Q3. The three stages are READ together during triage — you reason about deviation stats in conjunction with how many pixels survived rim-mask. One file is one jq invocation; three files mean three loads on every triage workflow. Top-level schema coupling is acceptable: the next time we add a stage (or change one) we bump the unified schema. |
| D3.a | **Schema bump on `10b-blob-template-scores.json`: v1→v2; rename `BlobIndex` → `BlobOrdinal` with all-blobs semantics.** `BlobFeat` gains an `int Ordinal` field set during `ConnectedComponents.Label` (8-connected emission order). Both `10b` (Icon-class scores) and `10c` (all-blobs classifications) cross-ref via the SAME ordinal — `10b` is sparse over the same ordinal space. | Brainstorm follow-up after Q3. Downstream analysis is performed by agent sessions reading the bundle. A divided ordinal space (Icon-only vs all-blobs) forces bbox-join cross-ref, which is one footgun away from misreading either ordinal as comparable. [mithril#1122](https://github.com/moumantai-gg/mithril/pull/1122)'s contract is ~12 hours old at the time of this spec (commit `0dace4ea`, merged 2026-06-09); the schema-version bump is cheap. |
| D4 | **Both orientations emitted, separate PNG suffixes (`07b-foreground.png` + `07b-r180-foreground.png`).** JSON records carry a `rotate180` field. `MapCalibrationSolveEngine.Solve` extends the existing [mithril#1121](https://github.com/moumantai-gg/mithril/issues/1121) Rotate180-wrap pattern to all four new sinks. | Brainstorm Q4. The detector is orientation-blind; the engine's two-pass orientation enumeration is the existing behaviour. Dropping the rotate180 pass would create asymmetry with `10b` (`BlobScoreSink` keeps both). Doubling 4→8 PNGs is fine — disk cost is bounded (~4× per attempt, attempts already write a half-dozen PNGs). Triage opens the false-orientation set 95% of the time; the rotate180 set is there for sanity-check. |
| D5 | **LogTrace mirror at per-stage stats AND per-blob Classify granularity.** Stage stats: 4 records × 2 orientations × (1 + 1 synthesis-J rim) ≤ 12 records/attempt. Blob Classify: ~23 blobs × 2 orientations ≤ 50 records/attempt in Hogan's. Total ≤ ~60 trace lines/attempt — well below the Palantir live-log budget. | Brainstorm Q5. Matches the [mithril#1121](https://github.com/moumantai-gg/mithril/issues/1121) cadence (per-(blob, template) NCC is even higher and was deemed fine). Live-log path lets a triager watch the pipeline in real-time without waiting for the bundle to write. Per-stage-only would force triagers to grep the JSON for every "which blob got Noise-classified?" question. |
| D6 | **Rim-mask sink fires from BOTH callers** of [`DeviationFloodRimMask.Build`](../../../src/Mithril.MapCalibration.Detection/DeviationFloodRimMask.cs): [`DeviationBlobCalibrationDetector.Detect`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs) AND [`IconLikelihoodField.LoadDeviationAsField`](../../../src/Mithril.MapCalibration.Detection/IconLikelihoodField.cs) (called from synthesis-J's L_t field builder). Records carry `pipeline ∈ { "blob_detection", "synthesis_j" }`. The other three sinks (deviation, morph, classify) remain blob-detection-only — those stages don't exist in the synthesis-J path. | Brainstorm Q6. The same `DeviationFloodRimMask` helper drives both production callers. Instrumenting only the blob-detection call site would leave a future synthesis-J rim-mask failure as silently un-debuggable as today's blob-detection failure. Synthesis-J's deviation map (additive `byte[]`) is a different shape from blob-detection's local-NCC `float[]`, so the deviation sink does NOT extend; only the rim-mask helper is shared. |
| D6.a | **`10c.rimMasks[]` is a single flat array with `pipeline` discriminator** (not split into `blobDetectionRimMask` / `synthesisJRimMask` sections). | The two pipelines emit `RimMaskSnapshot` records of identical shape; splitting duplicates schema. Flat array is one jq filter (`.rimMasks[] \| select(.pipeline=="synthesis_j")`) to slice. Co-located pipelines stay visible when a triager wants both. |
| D7 | **Static utilities stay structurally pure. Diagnostic threading is entirely at the orchestrator layer.** Decision logic, signatures, and behaviour of `LocalNccDeviation.DeviationMap`, `DeviationFloodRimMask.Build`, `Morphology.Close`, `ConnectedComponents.Label`, and `DeviationBlobDetector.Classify` are preserved. The one mechanical exception is `ConnectedComponents.Label` setting `BlobFeat.Ordinal = compIndex` during its existing loop — purely additive bookkeeping (not a decision branch), needed to back D3.a's unified ordinal space. The orchestrator (`DeviationBlobCalibrationDetector.Detect` and `MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation`) retains intermediate buffers and emits to sinks. `DeviationBlobDetector.DetectIconBlobs` gains one optional `DetectionDiagnosticHooks?` parameter and emits per-stage + per-blob (the `comps` loop emits ALL classifications, including Noise/Fog/Structure that today get filtered out). | The audit's lesson is "static utilities are unobservable" — honored by adding observability at the call site, not by refactoring otherwise-clean pure functions. The intermediate buffers (`dev`, `fg`, `rimMask`, `fg-after-morph`) are already locally computed; the orchestrator just retains them when a hook is wired. Zero structural cost to the helpers' decision paths. |
| D8 | **Backward-compat lock = one combined "output is identical with and without hooks" test on `DeviationBlobCalibrationDetector`** (analog of [mithril#1122](https://github.com/moumantai-gg/mithril/pull/1122)'s `Detector_output_is_identical_with_and_without_sink`). Wiring all four hooks alongside an empty hook must produce byte-identical `Detect(...)` output to the no-hook path. Plus the existing #1121 lock keeps protecting the BlobScoreSink path. | Per-sink locks add boilerplate without coverage gain. The combined test catches any hook side-effect — current concern is that the per-blob LogTrace mirror could legitimately call `_logger?.LogTrace(...)` with a parameter pack that triggers a heavyweight formatter path, but the test runs on a no-op logger fixture so the `LogTrace` path is also exercised null-safely. |
| D9 | **Zero producer cost when `Diagnostics == null`.** The orchestrator: `if (request.Diagnostics is null) { /* fast path: no buffer retention, no PNG-prep, no LogTrace formatting */ }`. The intermediate buffers are still allocated (they're load-bearing for the production decision path) but are NOT handed up to a context; they GC at function return. Trace mirror is bypassed via the `_logger?.LogTrace(...)` null-prop. | Matches CLAUDE.md's "producers emit unconditionally; recording is opt-in" convention. The orchestrator-side branch is the one piece that DOES need an `is null` check (because retaining buffers up the stack costs real memory). Bench-style verification of "zero cost" via the byte-equality test from D8 plus a smoke test that confirms allocation profile is unchanged. |
| D10 | **`MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation` becomes non-static and gains hooks threading.** Today's `internal static`; the synthesis-J rim-mask sink path needs orchestrator-level retention of the deviation byte[] (so the rim mask is computed ONCE per orientation rather than once-per-template, which would emit duplicate identical records). Refactor: pull rim-mask computation up out of `LoadDeviationAsField`'s per-template loop into the orchestrator body; pass a `bool[] rim` overload into `LoadDeviationAsField`. | The current code re-computes the rim mask once PER template, all with identical inputs — a latent perf issue this spec sidesteps by lifting the computation. Tests that exercise `BuildLikelihoodFieldsFromDeviation` directly need fixture-method-signature updates; verified during Task 6 of the plan. |

## 4. Architecture overview

```
DetectionRequest (existing)
├─ BlobScoreSink                                   ← #1121, unchanged
└─ Diagnostics? : DetectionDiagnosticHooks         ← #1123 new
   ├─ Action<DeviationSnapshot>? OnDeviation       ← blob-detection only
   ├─ Action<RimMaskSnapshot>? OnRimMask           ← BOTH callers (pipeline-tagged)
   ├─ Action<MorphSnapshot>? OnMorph               ← blob-detection only
   └─ Action<BlobClassification>? OnBlobClassified ← blob-detection only (ALL comps, not just Icons)

DeviationBlobCalibrationDetector.Detect            ← wires all four blob-side sinks;
                                                     retains float[] dev, bool[] fg-initial,
                                                     bool[] rimMask, bool[] fg-after-morph
                                                     when hooks != null
MapCalibrationSolveEngine.Solve                    ← already wraps BlobScoreSink for Rotate180;
                                                     extends wrapper to all four new sinks
                                                     (one Action<T> wrapper per sink type)
MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation
                                                   ← non-static (was static); computes rim mask
                                                     ONCE per orientation; emits OnRimMask with
                                                     pipeline="synthesis_j"; passes pre-computed
                                                     rim mask into LoadDeviationAsField
AutoCalibrationEngine.RunAttemptCoreAsync          ← creates four Lists, wires the four hooks,
                                                     snapshots into CalibrationAttemptContext
                                                     (mirror of #1121's BlobScoreSink wiring
                                                     at lines 660-705)

CalibrationAttemptContext (existing)
├─ BlobTemplateScores: IReadOnlyList<BlobTemplateScore>?  ← #1121, unchanged (now keyed on BlobOrdinal)
├─ DeviationSnapshots:    IReadOnlyList<DeviationSnapshot>?     ← up to 2 (×orientation)
├─ RimMaskSnapshots:      IReadOnlyList<RimMaskSnapshot>?       ← up to 4 (×orientation × pipeline)
├─ MorphSnapshots:        IReadOnlyList<MorphSnapshot>?         ← up to 2 (×orientation)
└─ BlobClassifications:   IReadOnlyList<BlobClassification>?    ← ~50 in Hogan's (×orientation × ALL comps)

FilesystemCalibrationAttemptBundleSink             ← extends AttemptFilesJson with 11 new file
                                                     slots; writes 10 PNGs + 10c JSON; updated
                                                     10b emission to use BlobOrdinal
```

Producer cost when `Diagnostics == null`: zero retention, zero PNG-prep, zero LogTrace formatting. The intermediate buffers stay locally computed and GC at function return.

Producer cost when `Diagnostics != null`: three `bool[~458 KB each]` retained per orientation (fg-initial, rimMask, fg-after-morph) + one `bool[~458 KB]` per orientation for the synthesis-J rim mask + small per-blob pixel-index lists (~56 KB total). Across two orientations: ~4 MB transient memory per accepted/rejected attempt. **`dev float[]` is NOT retained** — summary stats are computed at sink-emission time, serialised to JSON; the buffer is GC-eligible at `Detect`'s return. The bundle sink writes the PNGs synchronously on its existing path; the bool[] arrays are GC-eligible after the bundle write completes.

## 5. Layer-by-layer detail

### 5.1 Event records (in [`ICalibrationDetector.cs`](../../../src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs))

```csharp
/// <summary>
/// Aggregate of opt-in observability sinks for the deviation-blob detector pipeline
/// (mithril#1123). Threaded via DetectionRequest.Diagnostics. Each callback is
/// independently nullable; the orchestrator skips both retention and LogTrace
/// emission for the null sinks (producer-cost = zero per CLAUDE.md). Mirrors the
/// #1121 BlobScoreSink pattern, scaled to four upstream stages.
/// </summary>
public sealed record DetectionDiagnosticHooks(
    Action<DeviationSnapshot>? OnDeviation,
    Action<RimMaskSnapshot>? OnRimMask,
    Action<MorphSnapshot>? OnMorph,
    Action<BlobClassification>? OnBlobClassified);

/// <summary>
/// One observation per orientation pass. Emitted from inside
/// <see cref="DeviationBlobDetector.DetectIconBlobs"/> AFTER the threshold step
/// — that's where the fg-initial bool[] is produced and where the dev float[]
/// is still in scope for stats computation. Stats are computed at emission time
/// + serialised to JSON; the dev float[] is NOT retained on this record (today's
/// 07-deviation.png renders from a separate visualizer path, not this buffer).
/// The fg-initial bool[] IS retained — it backs 07b-foreground.png.
/// </summary>
public sealed record DeviationSnapshot(
    bool Rotate180,
    int Width,
    int Height,
    int Win,                  // local-NCC window size (constant 11 today)
    double Threshold,         // 1.0 - lowNcc; pixels with dev >= Threshold become fg-initial true
    double MeanNcc,           // already computed by LocalNccDeviation.DeviationMap's out param
    double Min, double Max, double Mean,
    double P50, double P95, double P99,
    int AboveThresholdCount,  // == count(true in ForegroundBuffer)
    bool[] ForegroundBuffer); // fg-initial bool[w*h]: dev[i] >= Threshold, BEFORE rim-subtract

/// <summary>
/// One observation per (orientation, pipeline) pair. Pipeline ∈
/// { "blob_detection", "synthesis_j" } discriminates the two callers of
/// DeviationFloodRimMask.Build. FgInputCount / FgSurvivorCount are populated
/// on the blob_detection path; synthesis_j supplies -1 sentinels (the synth
/// pipeline doesn't have an fg concept — it applies the rim mask to a
/// likelihood field).
/// </summary>
public sealed record RimMaskSnapshot(
    string Pipeline,          // "blob_detection" | "synthesis_j"
    bool Rotate180,
    int Width,
    int Height,
    double Threshold,
    int RimPixelCount,
    int FgInputCount,         // pixels fg before rim-subtract (blob_detection); -1 (synthesis_j)
    int FgSurvivorCount,      // same caveat
    bool[] RimMaskBuffer);

/// <summary>
/// One observation per orientation pass. CloseRadius is the configured morph-
/// close radius (1 in production today; 0 disables the stage entirely).
/// </summary>
public sealed record MorphSnapshot(
    bool Rotate180,
    int Width, int Height,
    int CloseRadius,
    int FgInputCount,         // pixels fg before morph close (after rim-subtract)
    int FgOutputCount,        // pixels fg after morph close
    bool[] FgAfterMorphBuffer);

/// <summary>
/// One observation per connected component (ALL components, not just Icon-class).
/// BlobOrdinal is the position in the 8-connected emission order from
/// ConnectedComponents.Label — the same ordinal carried by #1121's
/// BlobTemplateScore.BlobOrdinal (post-schema-bump per D3.a). Cross-ref between
/// 10c.blobs[] and 10b.scores[] is by ordinal.
/// </summary>
public sealed record BlobClassification(
    bool Rotate180,
    int BlobOrdinal,          // index over comps emission order (D3.a)
    int MinX, int MinY, int W, int H, int Area,
    double Cx, double Cy,
    double MeanDev, double PeakDev,
    double Solidity, double Aspect,
    string BlobClass,         // "Noise" | "Icon" | "Fog" | "Structure"
    IReadOnlyList<int> Pixels); // BlobFeat.Pixels passed through (flat row-major indices); source for 07e-blob-classification.png. NOT serialised to 10c JSON — bundle sink uses for PNG render only.
```

### 5.2 `DeviationBlobCalibrationDetector.Detect` + `DeviationBlobDetector.DetectIconBlobs` — wiring all four blob-side hooks

```
DeviationBlobCalibrationDetector.Detect:
1.  shotF, texF = LocalNccDeviation.ToGrayFloat(...)
2.  dev = LocalNccDeviation.DeviationMap(shotF, texF, w, h, 11, out meanNcc, addedOnly: true)
3.  blobs = DetectIconBlobs(dev, w, h, lowNcc, rim, opts, closeRadius: 1, hooks, meanNcc)
4.  for each blob in blobs: per-template NCC (#1121, unchanged — emits BlobTemplateScore via blob.Ordinal)

DeviationBlobDetector.DetectIconBlobs (inside step 3):
3a. fg-initial = threshold(dev, devThr)
3b. if hooks?.OnDeviation:
       emit DeviationSnapshot(rotate180: false, threshold: devThr, meanNcc, stats over dev[],
                              ForegroundBuffer: fg-initial)
       LogTrace("Deviation (rotate180=False): mean ncc=... above-threshold=... of N px ...")
3c. if rim == DeviationFlood:
       rimMask = DeviationFloodRimMask.Build(dev, w, h, devThr)
       if hooks?.OnRimMask:
         emit RimMaskSnapshot(pipeline: "blob_detection", rotate180: false, RimMaskBuffer: rimMask)
         LogTrace("RimMask (rotate180=False, pipeline=blob_detection): rim=... fg pre=... post=...")
       fg = fg-initial - rimMask
    else:
       fg = fg-initial
3d. if closeRadius > 0:
       fg-after-morph = Morphology.Close(fg, w, h, closeRadius)
       if hooks?.OnMorph: emit MorphSnapshot(... FgAfterMorphBuffer: fg-after-morph) + LogTrace
    else:
       fg-after-morph = fg
3e. comps = ConnectedComponents.Label(fg-after-morph, w, h, dev)   // sets f.Ordinal per D7 exception
3f. for each f in comps:
       cls = Classify(f, opts)                                      // static helper unchanged
       if hooks?.OnBlobClassified: emit BlobClassification(... Pixels: f.Pixels) + LogTrace per blob
       if cls == BlobClass.Icon: icons.Add(f)
    return icons
```

LogTrace lines (one example per stage):

```text
[Trace] Mithril.MapCalibration.Detection: Deviation (rotate180=False): mean ncc=0.812 above-threshold=18342 of 458329 px (threshold=0.450).
[Trace] Mithril.MapCalibration.Detection: RimMask (rotate180=False, pipeline=blob_detection): rim=4317 px, fg pre=18342 post=14025.
[Trace] Mithril.MapCalibration.Detection: Morph (rotate180=False): closeRadius=1 fg pre=14025 post=14987.
[Trace] Mithril.MapCalibration.Detection: Blob #7 (247,114,16,17) area=219 meanDev=0.612 peakDev=0.881 solidity=0.80 aspect=1.06 → Icon.
[Trace] Mithril.MapCalibration.Detection: Blob #11 (412,40,28,3) area=82 meanDev=0.488 peakDev=0.715 solidity=0.97 aspect=9.33 → Noise.
```

### 5.3 `DeviationBlobDetector.DetectIconBlobs` — signature + emission

### 5.3 `DeviationBlobDetector.DetectIconBlobs` — signature

Today's signature:

```csharp
public static IReadOnlyList<BlobFeat> DetectIconBlobs(
    float[] dev, int w, int h, double lowNcc, RimMaskMode rim, BlobOptions opts, int closeRadius)
```

Adds:

```csharp
public static IReadOnlyList<BlobFeat> DetectIconBlobs(
    float[] dev, int w, int h, double lowNcc, RimMaskMode rim, BlobOptions opts, int closeRadius,
    DetectionDiagnosticHooks? hooks = null,
    double meanNcc = double.NaN)
```

`meanNcc` is passed through to `DeviationSnapshot.MeanNcc` (the value is computed in `LocalNccDeviation.DeviationMap`'s `out` param — already in scope at the caller). `Rotate180` is left default-false; `MapCalibrationSolveEngine.Solve`'s wrapper rewrites it (see §5.4). Internal flow described in §5.2 step 3a–3f.

The static helpers themselves (`DeviationFloodRimMask`, `Morphology`, `ConnectedComponents`, `Classify`) are UNTOUCHED in their decision logic. The orchestrator (`DetectIconBlobs`) is where the sinks live, because that's where the intermediate buffers are owned. **D7 exception**: `ConnectedComponents.Label` sets `f.Ordinal = compIndex` during its existing loop (additive bookkeeping; no decision change).

### 5.4 `MapCalibrationSolveEngine.Solve` — extending the Rotate180 wrap

Today (post-#1121):

```csharp
var callerSink = request.BlobScoreSink;
var wrappedSink = callerSink is null
    ? null
    : (Action<BlobTemplateScore>)(score => callerSink(score with { Rotate180 = orientationFlag }));
var req = request with { BaseTexture = texture, BlobScoreSink = wrappedSink };
```

Extends to (local-copy idiom matches #1121; closure captures the hoisted locals, not the parent `Diagnostics` field — keeps the null-flow analyzer happy and the per-callback null check fast):

```csharp
var callerHooks = request.Diagnostics;
DetectionDiagnosticHooks? wrappedHooks = null;
if (callerHooks is not null)
{
    var devSink   = callerHooks.OnDeviation;
    var rimSink   = callerHooks.OnRimMask;
    var morphSink = callerHooks.OnMorph;
    var blobSink  = callerHooks.OnBlobClassified;
    wrappedHooks = new DetectionDiagnosticHooks(
        OnDeviation:      devSink   is null ? null : s => devSink  (s with { Rotate180 = orientationFlag }),
        OnRimMask:        rimSink   is null ? null : s => rimSink  (s with { Rotate180 = orientationFlag }),
        OnMorph:          morphSink is null ? null : s => morphSink(s with { Rotate180 = orientationFlag }),
        OnBlobClassified: blobSink  is null ? null : c => blobSink (c with { Rotate180 = orientationFlag }));
}

var callerBlobScoreSink = request.BlobScoreSink;
var wrappedBlobScoreSink = callerBlobScoreSink is null
    ? null
    : (Action<BlobTemplateScore>)(score => callerBlobScoreSink(score with { Rotate180 = orientationFlag }));

var req = request with { BaseTexture = texture, BlobScoreSink = wrappedBlobScoreSink, Diagnostics = wrappedHooks };
```

The detector itself emits `Rotate180 = false` on every record; the wrapper rewrites the flag per orientation pass.

### 5.5 `MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation` — synthesis-J rim-mask emission

Today (`internal static`, verified at [`MapCalibrationSolveEngine.cs:450-499`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs)):

```csharp
internal static IReadOnlyDictionary<string, double[,]> BuildLikelihoodFieldsFromDeviation(
    GrayImage screenshot, GrayImage baseTexture, IconTemplateSet templates,
    double typeFloor, int? renderSizePx)
{
    // build additive deviation byte[] (max(0, screenshot - baseTexture)); build devImage;
    // for each (type, template):
    //   fields[type] = IconLikelihoodField.LoadDeviationAsField(devImage, template, applyRimMask: true, devThr: DefaultDevThr);
}
```

Today's `LoadDeviationAsField(GrayImage, IconTemplate, bool applyRimMask, double devThr)` internally does (verified at [`IconLikelihoodField.cs:61-77`](../../../src/Mithril.MapCalibration.Detection/IconLikelihoodField.cs)): converts `byte[] deviation.Pixels` → `float[] dev` normalised to [0, 1], calls `DeviationFloodRimMask.Build(dev, w, h, devThr)`, zeroes rim pixels from a `maskedPixels byte[]`, and calls `ScoreAll(masked, template)`. **The byte→float conversion + rim-mask build happens once per template** — duplicate work today, an opportunity to lift to the orchestrator.

After (instance method, hook-aware, rim-mask lifted out of the per-template loop):

```csharp
private IReadOnlyDictionary<string, double[,]> BuildLikelihoodFieldsFromDeviation(
    GrayImage screenshot, GrayImage baseTexture, IconTemplateSet templates,
    double typeFloor, int? renderSizePx,
    bool rotate180,
    DetectionDiagnosticHooks? hooks)
{
    // build additive deviation byte[]; build devImage (unchanged from today).
    var devThr = IconLikelihoodField.DefaultDevThr;
    int n = w * h;
    var devAsFloat = new float[n];
    for (int i = 0; i < n; i++) devAsFloat[i] = devImage.Pixels[i] / 255f;
    var rim = DeviationFloodRimMask.Build(devAsFloat, w, h, devThr);

    if (hooks?.OnRimMask is not null)
    {
        hooks.OnRimMask(new RimMaskSnapshot(
            Pipeline: "synthesis_j",
            Rotate180: rotate180,
            Width: w, Height: h,
            Threshold: devThr,
            RimPixelCount: CountTrue(rim),
            FgInputCount: -1, FgSurvivorCount: -1,  // -1 sentinel: synth-j has no fg concept
            RimMaskBuffer: rim));
        _logger?.LogTrace(
            "RimMask (rotate180={Rotate180}, pipeline=synthesis_j): rim={Rim} of {N} px (threshold={T:0.000}).",
            rotate180, CountTrue(rim), w * h, devThr);
    }

    // per-template loop: use the pre-computed rim mask via a new LoadDeviationAsField overload.
    foreach (var (type, template) in perType)
    {
        fields[type] = IconLikelihoodField.LoadDeviationAsField(devImage, template, rim);
    }
}
```

The new `LoadDeviationAsField(GrayImage deviation, IconTemplate template, bool[] rim)` overload accepts a pre-computed rim mask; it does NOT take `devThr` (only used in rim-flood, which is now done by the caller). The existing 4-arg `(GrayImage, IconTemplate, bool applyRimMask, double devThr)` overload remains, calling the new one internally when `applyRimMask` is true (so non-instrumented call sites — synthetic-probe tooling — are unaffected). The convenience 2-arg overload `(GrayImage, IconTemplate)` is unchanged.

### 5.6 `AutoCalibrationEngine.RunAttemptCoreAsync` — context wiring

Mirrors the [mithril#1121](https://github.com/moumantai-gg/mithril/pull/1122) pattern at lines 660–705:

```csharp
// mithril#1123: collect deviation/rim/morph/classification observations for the
// diagnostic bundle. Engine layer creates the buffers; the detector layer (via the
// SolveEngine's rotate180 wrapper) appends one record per (stage, orientation).
// null hooks → zero producer cost.
var deviationSnaps   = new List<DeviationSnapshot>();
var rimSnaps         = new List<RimMaskSnapshot>();
var morphSnaps       = new List<MorphSnapshot>();
var blobClasses      = new List<BlobClassification>();
var hooks = new DetectionDiagnosticHooks(
    OnDeviation:      deviationSnaps.Add,
    OnRimMask:        rimSnaps.Add,
    OnMorph:          morphSnaps.Add,
    OnBlobClassified: blobClasses.Add);

var blobScores = new List<BlobTemplateScore>();
var request = new DetectionRequest(/* …existing args… */)
{
    RenderSizePx = RenderSizePx,
    BlobScoreSink = blobScores.Add,
    Diagnostics = hooks,
};
// …Solve runs…
attempt.BlobTemplateScores  = blobScores;
attempt.DeviationSnapshots  = deviationSnaps;
attempt.RimMaskSnapshots    = rimSnaps;
attempt.MorphSnapshots      = morphSnaps;
attempt.BlobClassifications = blobClasses;
```

## 6. Persistence — the bundle dump

### 6.1 PNG files (10 new — 8 blob-detection + 2 synthesis-J rim mask)

| Filename | Source field | Render mode |
|---|---|---|
| `07b-foreground.png` | blob-detection fg-initial (rotate180=false) | bool → black/white |
| `07b-r180-foreground.png` | blob-detection fg-initial (rotate180=true) | bool → black/white |
| `07c-rim-mask.png` | blob-detection rim mask (rotate180=false) | bool → black/white |
| `07c-r180-rim-mask.png` | blob-detection rim mask (rotate180=true) | bool → black/white |
| `07c-synth-rim-mask.png` | synthesis-J rim mask (rotate180=false) | bool → black/white |
| `07c-r180-synth-rim-mask.png` | synthesis-J rim mask (rotate180=true) | bool → black/white |
| `07d-morphed.png` | fg after morph close (rotate180=false) | bool → black/white |
| `07d-r180-morphed.png` | fg after morph close (rotate180=true) | bool → black/white |
| `07e-blob-classification.png` | per-pixel labelled by blob's `BlobClass` | colormap: Noise=dim-grey, Icon=green, Fog=blue, Structure=red, background=black |
| `07e-r180-blob-classification.png` | as above (rotate180=true) | as above |

The colormap PNG (`07e-*`) renders the `ConnectedComponents.Label` output: the bundle sink iterates `BlobClassifications` records, walks each record's `Pixels` flat-index list, and paints the colour of `BlobClass` into a `byte[w*h*4]` Bgra32 buffer. Pixels not covered by any record stay black. Triagers see the spatial layout of the entire pipeline outcome in one image.

**PNG write style.** All ten new PNGs are written inline via `BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, byteBuffer, w)` for the four bool[]-derived files (`bool[] → byte[]` via `true ? 255 : 0` is a tight loop) and `BitmapSource.Create(..., PixelFormats.Bgra32, null, bgraBuffer, w*4)` for `07e-*`. **No `IAttemptBundleVisualizer` extension needed** — the existing visualizer abstraction is for non-trivial rendering (it computes the deviation map en route to `07-deviation.png`); the new files are direct mask-or-label visualisations. Mirrors the existing in-line `BitmapSource.Create` pattern at `TryWriteGrayScreenshot` ([`FilesystemCalibrationAttemptBundleSink.cs:114-126`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs#L114)).

### 6.2 JSON file — `10c-blob-pipeline.json`

```json
{
  "schemaVersion": 1,
  "deviation": [
    {
      "rotate180": false,
      "width": 677, "height": 677,
      "win": 11,
      "threshold": 0.450,
      "meanNcc": 0.812,
      "min": 0.0, "max": 0.998, "mean": 0.187,
      "p50": 0.124, "p95": 0.673, "p99": 0.872,
      "aboveThresholdCount": 18342
    },
    { "rotate180": true, "...": "..." }
  ],
  "rimMasks": [
    {
      "pipeline": "blob_detection",
      "rotate180": false,
      "width": 677, "height": 677,
      "threshold": 0.450,
      "rimPixelCount": 4317,
      "fgInputCount": 18342,
      "fgSurvivorCount": 14025
    },
    { "pipeline": "synthesis_j", "rotate180": false, "rimPixelCount": 4221, "fgInputCount": -1, "fgSurvivorCount": -1, "...": "..." },
    { "pipeline": "blob_detection", "rotate180": true,  "...": "..." },
    { "pipeline": "synthesis_j",    "rotate180": true,  "...": "..." }
  ],
  "morph": [
    { "rotate180": false, "closeRadius": 1, "fgInputCount": 14025, "fgOutputCount": 14987, "...": "..." },
    { "rotate180": true,  "...": "..." }
  ],
  "blobs": [
    {
      "rotate180": false,
      "blobOrdinal": 7,
      "minX": 247, "minY": 114, "w": 16, "h": 17, "area": 219,
      "cx": 254.9, "cy": 122.3,
      "meanDev": 0.612, "peakDev": 0.881,
      "solidity": 0.80, "aspect": 1.06,
      "blobClass": "Icon"
    },
    {
      "rotate180": false,
      "blobOrdinal": 11,
      "minX": 412, "minY": 40, "w": 28, "h": 3, "area": 82,
      "cx": 425.9, "cy": 41.4,
      "meanDev": 0.488, "peakDev": 0.715,
      "solidity": 0.97, "aspect": 9.33,
      "blobClass": "Noise"
    }
    // …all comps, both orientations…
  ]
}
```

### 6.3 `AttemptFilesJson` extension

Adds 11 new optional `string?` slots (all default-null per the #1122 pattern, so pre-#1123 readers round-trip unchanged):

```csharp
public sealed record AttemptFilesJson(
    // …existing fields…
    string? BlobTemplateScores = null,    // #1121, unchanged
    // #1123 new file slots:
    string? BlobPipeline = null,          // 10c-blob-pipeline.json
    string? Foreground = null,            // 07b-foreground.png
    string? ForegroundR180 = null,        // 07b-r180-foreground.png
    string? RimMask = null,               // 07c-rim-mask.png
    string? RimMaskR180 = null,           // 07c-r180-rim-mask.png
    string? SynthRimMask = null,          // 07c-synth-rim-mask.png
    string? SynthRimMaskR180 = null,      // 07c-r180-synth-rim-mask.png
    string? Morphed = null,               // 07d-morphed.png
    string? MorphedR180 = null,           // 07d-r180-morphed.png
    string? BlobClassification = null,    // 07e-blob-classification.png
    string? BlobClassificationR180 = null // 07e-r180-blob-classification.png
);
```

### 6.4 `10b-blob-template-scores.json` schema v1→v2 (D3.a)

The `BlobIndex` field becomes `BlobOrdinal` with all-blobs semantics. `BlobTemplateScoresJson.SchemaVersion` bumps to 2. Round-trip test asserts the new field name + schema version. The `BlobFeat.Ordinal` field is set during `ConnectedComponents.Label`; `DeviationBlobCalibrationDetector.Detect`'s emission uses `blob.Ordinal` instead of the local-counter `blobIndex++`.

## 7. Error handling + status surface

The bundle sink follows the existing `try { … write … } catch (Exception ex) { _logger?.LogWarning(ex, "<filename> write failed"); return null; }` pattern (one per file). A failed PNG write reports the file slot as null in `01-attempt.json` and continues; the JSON write similarly. The detector's hook emission is wrapped in nothing — if a sink callback throws, the throw propagates out of `Detect(...)` and the engine catches it in its existing `RunAttemptCoreAsync` outer try/catch, marking the attempt as `Outcome="error"`.

LogTrace formatting failures don't affect detection output — `_logger?.LogTrace(...)` follows MEL's standard "swallow formatter exceptions" contract.

No new diagnostics UI surface. Settings, perf-trace UI, and Palantir live-log all inherit the new categories/sinks via existing wiring.

## 8. Testing strategy

| Test | Project | Asserts |
|---|---|---|
| `Detector_output_is_identical_with_and_without_hooks` | `Mithril.MapCalibration.Tests` | Driving `Detect(...)` with all four hooks wired vs all null produces byte-equal `IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>>`. Combined backward-compat lock (D8). |
| `OnDeviation_fires_once_per_orientation` | `Mithril.MapCalibration.Tests` | Engine-level test wraps `MapCalibrationSolveEngine.Solve`; asserts exactly 2 `DeviationSnapshot` records, one per orientation flag, with `ForegroundBuffer.Length == W*H` and `AboveThresholdCount == count(true in ForegroundBuffer)`. |
| `OnRimMask_fires_per_orientation_per_pipeline` | `Mithril.MapCalibration.Tests` | Engine with `SynthesisRerankMode=Shadow` (prod default); asserts ≥1 record per (orientation, pipeline) tuple; `pipeline` ∈ `{"blob_detection","synthesis_j"}`; `RimMaskBuffer.Length == W*H`. |
| `OnMorph_fires_once_per_orientation` | `Mithril.MapCalibration.Tests` | Asserts exactly 2 `MorphSnapshot` records; `FgInputCount` matches `RimMaskSnapshot.FgSurvivorCount` of the blob-detection pipeline pass. |
| `OnBlobClassified_fires_for_all_comps_not_just_Icons` | `Mithril.MapCalibration.Tests` | Asserts the number of `BlobClassification` records ≥ `1` AND that the set of distinct `BlobClass` values includes at least one non-`Icon` value on the test fixture (`BuildPair` fixture is designed to produce Noise + Icon — extend if needed). |
| `BlobOrdinal_cross_refs_10b_and_10c` | `Mithril.MapCalibration.Tests` | Asserts every `BlobTemplateScore.BlobOrdinal` value in #1121's emission corresponds to a `BlobClassification.BlobOrdinal` value where `BlobClass == "Icon"`. Cross-file ordinal-space consistency (D3.a). |
| `BlobOrdinal_is_set_by_ConnectedComponents_Label` | `Mithril.MapCalibration.Tests` | Direct unit test of the static helper: feeds a 4×4 image with two disjoint components; asserts `f.Ordinal` is 0 and 1 in emission order. |
| `Synthesis_J_rim_mask_pipeline_tag` | `Mithril.MapCalibration.Tests` | Direct test of `MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation` with hooks wired; asserts the emitted `RimMaskSnapshot.Pipeline == "synthesis_j"`. |
| `LoadDeviationAsField_overload_uses_supplied_rim` | `Mithril.MapCalibration.Tests` | Asserts the new `(GrayImage deviation, IconTemplate template, bool[] rim)` overload produces the same L_t field as the existing `(deviation, template, applyRimMask: true, devThr)` overload when the supplied rim mask matches the freshly-built one. |
| `Writes_blob_pipeline_json_and_pngs_when_context_populated` | `Mithril.MapCalibration.Capture.Tests` | Mirror of [#1122's `Writes_blobTemplateScores_when_context_populated`](../../../tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs); asserts 10c JSON + 10 PNGs land; asserts `01-attempt.json.Files.BlobPipeline == "10c-blob-pipeline.json"` and the PNG slots are populated. |
| `Omits_blob_pipeline_when_context_null_or_empty` | `Mithril.MapCalibration.Capture.Tests` | Mirror of #1122's omit test; asserts no 10c JSON, no PNGs, `Files.BlobPipeline == null`. |
| `BlobTemplateScores_uses_BlobOrdinal_schema_v2` | `Mithril.MapCalibration.Capture.Tests` | Existing #1122 round-trip test, updated: asserts `SchemaVersion == 2`, field name is `BlobOrdinal` (not `BlobIndex`), and the round-tripped value matches the input. |

No per-site test ("does this `LogTrace` line fire?"). Per CLAUDE.md "don't write speculative guards" — the logging is its own assertion; if a future change drops a trace line, the next investigation re-files the issue.

## 9. Files touched

### 9.1 `src/`

| File | Change |
|---|---|
| [`src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs`](../../../src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs) | Add `DetectionDiagnosticHooks` record + four event records (§5.1). Add `Diagnostics` init-only property on `DetectionRequest`. Rename `BlobTemplateScore.BlobIndex` → `BlobOrdinal` with all-blobs semantics (D3.a). |
| [`src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs) | Retain `dev` after `DeviationMap`; emit `OnDeviation`. Thread hooks into `DetectIconBlobs` call. Use `blob.Ordinal` instead of local counter when emitting `BlobTemplateScore`. LogTrace mirror per stage. |
| [`src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs) | `DetectIconBlobs` gains `DetectionDiagnosticHooks? hooks = null, bool rotate180 = false` parameters. Emit `RimMaskSnapshot` (blob_detection), `MorphSnapshot`, `BlobClassification` (for ALL comps). `BlobFeat` gains `int Ordinal` field. **`Morphology`, `ConnectedComponents`, `Classify` static helpers themselves are untouched** — `ConnectedComponents.Label` sets `f.Ordinal` to the loop counter as part of its emission. |
| [`src/Mithril.MapCalibration.Detection/IconLikelihoodField.cs`](../../../src/Mithril.MapCalibration.Detection/IconLikelihoodField.cs) | Add `LoadDeviationAsField(GrayImage deviation, IconTemplate template, bool[] rim)` overload — no `devThr` (only used in rim-flood which the caller now owns). Existing 4-arg overload remains; when `applyRimMask: true` it builds the rim mask internally then delegates to the new overload. Convenience 2-arg overload unchanged. |
| [`src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs) | `Solve` extends the existing Rotate180-wrap to all four new sinks (§5.4). `BuildLikelihoodFieldsFromDeviation` becomes non-static; emits `OnRimMask` with `pipeline="synthesis_j"` once per orientation; passes pre-computed rim mask into the new `LoadDeviationAsField` overload (§5.5). |
| [`src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs) | Create four `List<T>` collectors, wire `DetectionDiagnosticHooks` on the request, snapshot into `CalibrationAttemptContext` (§5.6). Mirror of the existing #1121 BlobScoreSink wiring. |
| [`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs) | Add four `IReadOnlyList<T>?` properties: `DeviationSnapshots`, `RimMaskSnapshots`, `MorphSnapshots`, `BlobClassifications`. |
| [`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs) | Add 11 `string?` file slots to `AttemptFilesJson` (§6.3). Add `BlobPipelineJson` DTO + nested record DTOs (`DeviationSectionJson`, `RimMaskSectionJson`, `MorphSectionJson`, `BlobJson`). Register via `[JsonSerializable]`. Bump `BlobTemplateScoresJson.SchemaVersion = 2`; rename JSON field. |
| [`src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs) | Add `TryWriteBlobPipelineJson` (one JSON write), `TryWriteForegroundPng`, `TryWriteRimMaskPng`, `TryWriteMorphedPng`, `TryWriteBlobClassificationPng` (each takes orientation + pipeline params). Wire into the per-attempt write block. |

### 9.2 `tests/`

| File | Change |
|---|---|
| [`tests/Mithril.MapCalibration.Tests/Detection/DeviationBlobCalibrationDetectorTests.cs`](../../../tests/Mithril.MapCalibration.Tests/Detection/DeviationBlobCalibrationDetectorTests.cs) | Add the 8 sink-fires-when-wired + backward-compat-lock + ordinal-cross-ref tests (§8). Update existing #1121 tests that reference `BlobIndex` to `BlobOrdinal`. |
| [`tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineSynthesisRimSinkTests.cs`](../../../tests/Mithril.MapCalibration.Tests/Detection/) | **new** — direct test of `BuildLikelihoodFieldsFromDeviation`'s rim-mask emission; covers the synthesis-J pipeline tag. |
| [`tests/Mithril.MapCalibration.Tests/Detection/IconLikelihoodFieldOverloadTests.cs`](../../../tests/Mithril.MapCalibration.Tests/Detection/) | **new** — `LoadDeviationAsField` overload parity test. |
| [`tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs`](../../../tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs) | Add `Writes_blob_pipeline_*` + `Omits_blob_pipeline_*` tests. Update the existing #1122 round-trip test to assert schema v2 + `BlobOrdinal`. |

### 9.3 `docs/`

- [`docs/planning/calibration-pipeline-observability-1123/spec.md`](spec.md) — this file
- [`docs/planning/calibration-pipeline-observability-1123/plan.md`](plan.md) — task-by-task implementation plan
- [`docs/planning/INDEX.md`](../INDEX.md) — append the slug row

## 10. Out of scope

- **The NPC pip recall fix.** This pass is the observability prerequisite. [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) tracks the symptom; the fix lands once this data lets a triager pick the right cause.
- **The whole-image fallback detector** ([`WholeImageTemplateDetector`](../../../src/Mithril.MapCalibration.Detection/WholeImageTemplateDetector.cs)). Different path; separate audit if it needs one.
- **Refactoring static utilities to instance classes** with `ILogger`. D7 chose the orchestrator-level seam; the helpers stay pure.
- **Per-pixel observability** inside the static helpers (callbacks fired from inside `LocalNccDeviation`'s inner loop, for example). Would shred the perf budget.
- **Synthesis-J coverage beyond rim-mask.** No `OnDeviation`/`OnMorph`/`OnBlobClassified` for the synth-j path — those stages don't exist in that pipeline; their absence is by design, not omission.
- **Comparison overlay PNGs** ("deviation with rim mask superimposed"). The 4-PNG-per-orientation set is the minimal complete view; comparison renderings are a downstream tool concern.
- **Engine / consumer chain logging.** Already covered by [mithril#1093](https://github.com/moumantai-gg/mithril/issues/1093) (the consumer-chain pass).

## 11. Verification owed

| Claim | How to verify |
|---|---|
| The bundle sink's PNG-write path supports `bool[]` masks at the right pixel format. Today's `TryWriteDeviation` ([`FilesystemCalibrationAttemptBundleSink.cs:170-179`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs#L170)) goes through `IAttemptBundleVisualizer.RenderDeviation` and returns a `BitmapSource`; `TryWriteGrayScreenshot` ([line 114](../../../src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs#L114)) writes a `byte[]` via `BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, pixels, stride)`. The new Try*Png methods follow the Gray8 pattern (bool[] → byte[] conversion) inline. **Stack is WPF/`System.Windows.Media.Imaging`**, NOT `System.Drawing`. | Task 1 step 1: read the existing pattern; the bool[]→byte[] conversion is a 1-liner. The colormap `07e-*` PNG uses `PixelFormats.Bgra32`. |
| Making `MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation` non-static doesn't break fixture-side test reach. Grep result: ONE external `tests/` reference — [`SynthesisRerankFieldEquivalenceTests.cs:107`](../../../tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs#L107) calls it as `MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation(...)`. Other matches are in `docs/superpowers/plans/` (markdown, not code). | Task 6 step 0: re-grep `BuildLikelihoodFieldsFromDeviation` under `tests/`; update the one call to use an instance reference (the test must construct a `MapCalibrationSolveEngine` to access it — its existing fixture constructs detectors but not engines yet). |
| `IconLikelihoodField.LoadDeviationAsField`'s caller inventory beyond `BuildLikelihoodFieldsFromDeviation`. Grep across `src/` AND `tools/` AND `tests/`: one production caller in `src/` (`MapCalibrationSolveEngine.cs:493`), one tool caller in [`tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs:244`](../../../tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs), several test callers in `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/` + `tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs`. **All callers use one of the existing two overloads (`(deviation, template)` or `(deviation, template, applyRimMask, devThr)`); the new 3-arg `(deviation, template, rim)` overload is added without modifying any existing caller.** | Task 4 step 0: re-grep across all three directories to confirm no missing caller. |
| The bundled `BlobFeat.Ordinal` field doesn't break existing in-pipeline consumers of `BlobFeat`. | Task 2 step 0: grep `BlobFeat` field reads under `src/` and `tests/`; the field is additive — no consumer should fail. |
| The synthesis-J rim mask computed once at the orchestrator level produces an identical mask to the per-template call site today (preventing behavioural change). | Task 6 step 3: byte-equality test in `IconLikelihoodFieldOverloadTests.cs` (§9.2). |
| Memory budget: ~4 MB transient per attempt (three bool[~458 KB] × 2 orientations blob-detection + one bool[~458 KB] × 2 orientations synthesis-J rim + ~56 KB blob-pixel lists). Confirm GC pressure isn't a regression for back-to-back attempts under the existing `AutoCalibrationTrigger` cadence. | Post-merge: a 3-attempt sequence with the Hogan's screenshot via the manual-trigger hotkey; observe `Mithril` working-set in Task Manager during. |

## 12. Cross-references

- [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) — Hogan's Basement cal-quality umbrella; the live symptom this observability unblocks.
- [mithril#1121](https://github.com/moumantai-gg/mithril/issues/1121) / [mithril#1122](https://github.com/moumantai-gg/mithril/pull/1122) — per-blob/per-template NCC scoring (shipped). The canonical pattern this spec extends.
- [mithril#1093](https://github.com/moumantai-gg/mithril/issues/1093) — consumer-chain logging pass that explicitly scoped out the engine; the gap that originated this work.
- [mithril#1117](https://github.com/moumantai-gg/mithril/issues/1117) — synthesis-J shadow-mode observability (shipped). Sibling-not-overlap: #1117 covers the J/RefsAboveHalf scoring; this spec covers the deviation/rim/morph/classify stages.
- [mithril#1107](https://github.com/moumantai-gg/mithril/issues/1107) / CLAUDE.md "Instrumentation is not optional" bullet — the canonical instrumentation-gap failure mode CLAUDE.md cites.
- User memory [`instrumentation-surveys-include-static-utilities`](file:///C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/instrumentation_surveys_include_static_utilities.md) — the audit lesson load-bearing for this work.
- [`docs/planning/calibration-logging-pass-1093/spec.md`](../calibration-logging-pass-1093/spec.md) — structural template for this spec.
