# Rescale templates in the synthesis-J L_t field builder (#1022)

**Status:** active · **Issue:** [mithril#1022](https://github.com/moumantai-gg/mithril/issues/1022)

## Background

The synthesis-J Shadow-mode re-rank (#999, commit `bd01a193`, 2026-06-02) added a per-orientation L_t field build inside the auto-calibration solve path:

[`src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs:94`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs#L94)
```csharp
var fields = BuildLikelihoodFieldsFromDeviation(req.Screenshot, req.BaseTexture, req.Templates);
```

[`MapCalibrationSolveEngine.cs:351-388`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs#L351-L388) dedups `req.Templates.Templates` per `LandmarkType` and feeds each one into `IconLikelihoodField.LoadDeviationAsField`, which calls `ScoreAll` — naive sliding NCC, `O(W · H · tw · th)` per template.

PG ships icon sprites at native sprite resolution (235–256 px) and renders map icons on-screen at a single small size — the gate-study verdict pinned this at `RenderSizePx = 16`. The detector path already accounts for this:

[`src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs:45-52`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs#L45-L52)
```csharp
// PG ships icon sprites at native resolution (~256 px) but renders map
// icons at a single small on-screen size (~16 px). Single-scale NCC only
// correlates at matching size, so the templates MUST be downscaled to the
// render size before the per-blob match — otherwise every native-res
// template is larger than the blob crop and skipped, yielding zero
// detections (mithril#916). Returns the templates unchanged when they're
// already small (the synthetic-fixture path).
var templates = IconRenderScaler.RenderSized(request.Screenshot, request.Templates.Templates, request.TypeFloor, request.RenderSizePx);
```

The synthesis path was lifted without this guard.

## Symptom

Two real-run windows on the Eltibule test bench (`%LocalAppData%/Mithril/Shell/logs/mithril-2026060{2,3}.json`):

| Run | Refine | Solve | Total → monotonicity gate |
|---|---|---|---|
| 2026-06-02 03:09 UTC (pre-#999) | 4598 ms (NCC ladder) | **337 ms** | ~5 s |
| 2026-06-02 17:10 UTC (post-#999, NCC ladder) | 5153 ms | **52162 ms** | ~57 s |
| 2026-06-03 01:14 UTC (post-FM cutover #1009) | **298 ms** | **75303 ms** | ~76 s |

The FM cutover (#1009) delivered its 17× refine speedup (5000 ms → 298 ms); the 50–75 s now lives entirely in `solve`, against the spec's per-template budget of `≈30 ms`. Worse, the L_t fields are **mostly zero**: a 256 px template slid over a ~621 px crop falls off the edges at almost every position (the `ScoreAll` border-skip zeros those positions), so the synthesis-J telemetry being accumulated under Phase C of #999 is being scored on degenerate fields and isn't a meaningful signal.

The legacy inlier gate is still source-of-truth in Shadow mode, so persistence is unaffected — but every auto-calibration attempt pays the cost, and any decision to flip the synthesis path from Shadow to Enabled (mode = `SynthesisRerankMode.Enabled`) would be built on a broken signal.

## Cost back-of-envelope

`IconLikelihoodField.ScoreAll` is naive sliding NCC: `O(W · H · tw · th)` per template, parallelized over rows.

| Template size | Inner work / position | Per ScoreAll (W=621, H=617) | × 4 templates × 2 orientations |
|---|---|---|---|
| 16 × 16 (probe / detector) | 2 · 256 = 512 ops | ~196 M ops | ~1.6 B ops → spec's ~30 ms × 8 = ~200 ms |
| 256 × 252 (production) | 2 · 64,500 = 129k ops | ~50 B ops | ~400 B ops → ~50–75 s observed ✓ |

## Why the tests don't catch this

`SynthesisRerankFieldEquivalenceTests` ([`tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs`](https://github.com/moumantai-gg/mithril/blob/main/tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs)) only checks that production and probe paths produce byte-identical fields *given the same template*. All synthetic test fixtures (`SyntheticMap.DefaultIcons`) use 18–40 px templates, which are below `IconRenderScaler.ScaleSearchThresholdPx = 64` — so `RenderSized` is a no-op for the tests, and the bug never manifests under the synthetic suite. Real-asset replay (Phase 1 of the gate study) caught the same class of bug for the detector in [mithril#916](https://github.com/moumantai-gg/mithril/issues/916); the synthesis-J lift didn't inherit that lesson.

## Proposed change

A single change inside `Mithril.MapCalibration.Detection`. Two edits, one test addition.

### 1. Rescale inside `BuildLikelihoodFieldsFromDeviation`

Add `typeFloor` and `renderSizePx` parameters to the method, and rescale templates with `IconRenderScaler.RenderSized` before the per-type dedup loop. The call shape mirrors `DeviationBlobCalibrationDetector.cs:52` so both detection and synthesis-J score against identical template inputs.

The method also changes visibility from `private static` → `internal static` so the regression test (#3 below) can call it directly. [Mithril.MapCalibration.Detection.csproj:23](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj#L23) already declares `<InternalsVisibleTo Include="Mithril.MapCalibration.Tests" />`, so no csproj edit is needed.

```csharp
internal static IReadOnlyDictionary<string, double[,]> BuildLikelihoodFieldsFromDeviation(
    GrayImage screenshot,
    GrayImage baseTexture,
    IconTemplateSet templates,
    double typeFloor,
    int? renderSizePx)
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

    // PG ships icon sprites at native resolution (~256 px) but renders map icons
    // at a single small on-screen size (~16 px). Single-scale NCC only correlates
    // at matching size, so the templates MUST be downscaled to the render size
    // before sliding — otherwise every native-res template is larger than its
    // viable search area and produces a mostly-zero L_t (mithril#1022). Mirrors
    // DeviationBlobCalibrationDetector.cs:52. Returns templates unchanged when
    // they're already small (the synthetic-fixture path).
    var rescaled = IconRenderScaler.RenderSized(screenshot, templates.Templates, typeFloor, renderSizePx);

    // One template per landmark-type — the per-type L_t fields are keyed by
    // LandmarkType. If a type has multiple templates (e.g. variants), the LAST
    // in iteration order wins, matching the probe's path at SynthesisProbePhase.cs
    // (fieldsByType[template.LandmarkType] = ... inside a foreach). Production
    // must match this so Task 17's L_t equality test holds in any future
    // multi-template-per-type scenario.
    var perType = new Dictionary<string, IconTemplate>(StringComparer.Ordinal);
    foreach (var template in rescaled)
    {
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

### 2. Propagate from the callsite

Update [`MapCalibrationSolveEngine.cs:94`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs#L94) to pass through the two new arguments — they already live on the `DetectionRequest` the engine received:

```csharp
var fields = BuildLikelihoodFieldsFromDeviation(
    req.Screenshot, req.BaseTexture, req.Templates,
    req.TypeFloor, req.RenderSizePx);
```

No new state introduced; same values the detector pulls from the same `DetectionRequest`.

### 3. Regression test

Add a test alongside `SynthesisRerankFieldEquivalenceTests` (same folder; sibling class is fine, since the existing class is a single-fact test):

- Build a screenshot/base pair containing a known bright deviation at a predictable anchor (use `SyntheticMap.BlitTeardrop` like the existing test).
- Construct a single `IconTemplate` at PG-native sprite size (≥235 px on its longest dim — large enough to exceed `IconRenderScaler.ScaleSearchThresholdPx = 64`).
- **Path A (production):** call `MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation` directly with the native-res template and `renderSizePx: 16`. The method is `internal static` after edit #1, and the test project already has `InternalsVisibleTo` access.
- **Path B (expected):** manually rescale the same template via `IconRenderScaler.RenderSized(screenshot, [template], typeFloor: 0.0, pinnedSize: 16)`, then call `IconLikelihoodField.LoadDeviationAsField` against the same deviation.
- Assert byte-equivalent fields, matching the existing test's per-cell `.Should().Be(...)` style.

With the bug present, Path A's field is mostly-zero and the assertion fires. With the fix, A and B match byte-for-byte. The existing `SyntheticMap.DefaultIcons`-based test (small fixtures) stays untouched and continues to assert the no-rescale path.

## Acceptance criteria

1. Live Eltibule auto-calibration `solve finished in {Elapsed} ms` log line drops from ~50–75 s to **under 1 s** on the test bench (the user's local capture loop — same setup as #964 / #965).
2. The new regression test passes on the fix branch; the same test (cherry-picked against `main`) fails.
3. Existing tests stay green — in particular `SynthesisRerankFieldEquivalenceTests`, `SynthesisRerankShadowModeTests`, `SyntheticLargeTemplateEndToEndTests`, and `ReplayFixtureTests`.

## Explicitly out of scope (deferred)

- **Re-baselining `MapCalibrationSolverOptions.JMin` / `NMin` defaults.** The #1022 body marked this `Optional`: the thresholds were calibrated against #993's bundle dataset (which used probe-rescaled templates), so they should already be correct. We'll file a follow-up issue if Phase-C synthesis-J telemetry post-fix shows the thresholds drifted.
- **Hoisting the rescale to `Solve(...)` entry** so it runs once across both orientations rather than per-orientation inside the field builder. Marginal win — once templates ≤16 px, the per-orientation rescale call inside `RenderSized` is a no-op short-circuit (`maxTemplateDim <= ScaleSearchThresholdPx → return templates`), so the duplicate work after the first rescale is bounded to one comparison. Not worth the broader callsite change for a regression fix.
- **Fail-loud assertion that L_t fields are non-degenerate** at runtime. Tempting given the symptom, but would couple the solver to a behavioural invariant that's hard to express precisely (`max(field) > 0` is too weak, anything sharper is fixture-coupled). The regression test covers this at the unit-test layer.

## Related

- [#999](https://github.com/moumantai-gg/mithril/issues/999) — synthesis-J Shadow mode (the regression window).
- [#1009](https://github.com/moumantai-gg/mithril/issues/1009) — FM cutover. Landed in the same week but unrelated to this slowness; it fixed the *previous* 5 s NCC-ladder refine cost.
- [#916](https://github.com/moumantai-gg/mithril/issues/916) — the earlier "lift forgot to port the rescale" precedent for the detector. Same root cause; this is the synthesis-side recurrence.
- [#1028](https://github.com/moumantai-gg/mithril/pull/1028) — detection-project split. File paths in the #1022 body use the pre-split URLs; live code is now under `src/Mithril.MapCalibration.Detection/`.
