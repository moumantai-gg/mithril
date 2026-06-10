# Calibration detector-pipeline observability — plan

**Spec:** [`spec.md`](spec.md). **Issue:** [mithril#1123](https://github.com/moumantai-gg/mithril/issues/1123). **Branch:** `claude/sad-jemison-4abb0e` for this docs-only spec+plan PR; the implementation lands on a fresh feature branch the next session creates.

One PR, eleven tasks, ordered so each commit reads independently. The D3.a schema preamble lands first (no consumer yet — a backward-compat amendment to #1122). New types land before callers. The pipeline is wired bottom-up: deepest static-utility orchestrator first (`DetectIconBlobs`), then `Detect`, then `Solve`, then engine, then persistence, then the closing engine integration.

## Task 0 — D3.a schema preamble: `BlobIndex` → `BlobOrdinal`, `10b` schema v1→v2

**Files:** [`src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs`](../../../src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs), [`src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs), [`src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs), [`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs), [`src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs).

**Steps:**

1. **Rename `BlobTemplateScore.BlobIndex` → `BlobOrdinal`** in `ICalibrationDetector.cs:73-88`. Update the `<remarks>` doc to reflect "index over all components in `ConnectedComponents.Label`'s 8-connected emission order — same ordinal space as `BlobClassification.BlobOrdinal` introduced in #1123."
2. **Add `int Ordinal` field to `BlobFeat`** in `DeviationBlobDetector.cs:96-109`. Default 0 — set by `ConnectedComponents.Label` during emission.
3. **`ConnectedComponents.Label` sets `f.Ordinal`** at the top of each `comps.Add(f)` cycle. Current loop at `DeviationBlobDetector.cs:117-152`: insert `f.Ordinal = comps.Count;` before `comps.Add(f);`. Zero-cost bookkeeping.
4. **`DeviationBlobCalibrationDetector.Detect` uses `blob.Ordinal`** at `DeviationBlobCalibrationDetector.cs:90-105` — replace the local `int blobIndex = 0; … blobIndex++;` counter with reads of `blob.Ordinal` in the `EmitDiagnostic` call. Drop the local counter entirely.
5. **`BlobTemplateScoreJson.BlobIndex` → `BlobOrdinal`** in `CalibrationBundleJson.cs:144`. Bump `BlobTemplateScoresJson.SchemaVersion` literal to 2.
6. **Bundle sink update** at `FilesystemCalibrationAttemptBundleSink.cs:257`: `BlobIndex: s.BlobIndex` → `BlobOrdinal: s.BlobOrdinal`. Update `SchemaVersion: 1` → `SchemaVersion: 2` at line 272.

**Tests:** Update three existing #1122 test sites:

- [`tests/Mithril.MapCalibration.Tests/Detection/DeviationBlobCalibrationDetectorTests.cs:98`](../../../tests/Mithril.MapCalibration.Tests/Detection/DeviationBlobCalibrationDetectorTests.cs#L98) — `s.BlobIndex` → `s.BlobOrdinal`.
- [`tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs:215`](../../../tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs#L215) — `dto.Scores[0].BlobIndex.Should().Be(0)` → `dto.Scores[0].BlobOrdinal.Should().Be(0)`. Plus assert `dto.SchemaVersion.Should().Be(2)` (was implicit-1).
- Same file ctor calls in `Writes_blobTemplateScores_when_context_populated` (lines 175-205): `BlobIndex: 0, BlobIndex: 0, BlobIndex: 1` → `BlobOrdinal: 0, BlobOrdinal: 0, BlobOrdinal: 1`.

**Add one new test** in `DeviationBlobCalibrationDetectorTests.cs`:

```csharp
[Fact]
public void BlobOrdinal_is_set_by_ConnectedComponents_Label_emission_order()
{
    // 4x4 grid with two disjoint pixels in the deviation map's foreground band:
    //   one at (1,1) area=1, one at (3,3) area=1.
    // ConnectedComponents.Label visits (1,1) first (raster scan), then (3,3).
    // So Ordinal=0 for the first, Ordinal=1 for the second.
    var dev = new float[16];
    dev[1 * 4 + 1] = 1.0f;   // above threshold
    dev[3 * 4 + 3] = 1.0f;
    var opts = new BlobOptions(MinArea: 1, MaxIconArea: 100, MinSolidity: 0.0, MaxAspect: 100.0, MinPeak: 0.5);

    var blobs = DeviationBlobDetector.DetectIconBlobs(dev, 4, 4, lowNcc: 0.5, RimMaskMode.None, opts, closeRadius: 0);
    blobs.Should().HaveCount(2);
    blobs[0].Ordinal.Should().Be(0);
    blobs[1].Ordinal.Should().Be(1);
}
```

**Acceptance:** `dotnet build Mithril.slnx` green. `dotnet test tests/Mithril.MapCalibration.Tests tests/Mithril.MapCalibration.Capture.Tests` green. The renamed JSON field is now `"blobOrdinal"` (camelCase via the existing `JsonKnownNamingPolicy.CamelCase` policy). No new files added; pure rename + ordinal-field addition.

---

## Task 1 — `DetectionDiagnosticHooks` vocabulary

**Files:** [`src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs`](../../../src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs).

**Steps:**

1. Append the five new types per spec §5.1: `DetectionDiagnosticHooks` aggregate record + `DeviationSnapshot` + `RimMaskSnapshot` + `MorphSnapshot` + `BlobClassification`. All `public sealed record`s with positional constructors.
2. Add `Diagnostics` init-only property to `DetectionRequest`:
   ```csharp
   /// <summary>
   /// Optional diagnostic hooks for the deviation-blob detector pipeline
   /// (mithril#1123). Sibling to <see cref="BlobScoreSink"/>; null in tests
   /// + production paths that don't need the per-stage observability. Producer
   /// cost is zero when null.
   /// </summary>
   public DetectionDiagnosticHooks? Diagnostics { get; init; }
   ```

**Tests:** None — pure type addition. Build green is the assertion.

**Acceptance:** `dotnet build Mithril.slnx` green. No callers reference the new types yet; zero behavioural diff. The `BlobScoreSink` shipped by #1121 stays as a sibling field, unchanged.

---

## Task 2 — `DetectIconBlobs` emits all four stages

**Files:** [`src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs).

**Steps:**

1. Extend `DetectIconBlobs` signature per spec §5.3:
   ```csharp
   public static IReadOnlyList<BlobFeat> DetectIconBlobs(
       float[] dev, int w, int h, double lowNcc, RimMaskMode rim, BlobOptions opts, int closeRadius,
       DetectionDiagnosticHooks? hooks = null,
       double meanNcc = double.NaN)
   ```
2. After building `fg-initial` (after the threshold loop), emit `OnDeviation` + `LogTrace`:
   ```csharp
   if (hooks?.OnDeviation is not null)
   {
       var (min, max, mean, p50, p95, p99) = ComputeDeviationStats(dev);  // helper, private static
       int aboveCount = 0;
       for (int i = 0; i < n; i++) if (fg[i]) aboveCount++;
       hooks.OnDeviation(new DeviationSnapshot(
           Rotate180: false,
           Width: w, Height: h, Win: 11,
           Threshold: devThr, MeanNcc: meanNcc,
           Min: min, Max: max, Mean: mean,
           P50: p50, P95: p95, P99: p99,
           AboveThresholdCount: aboveCount,
           ForegroundBuffer: (bool[])fg.Clone()));   // clone — caller's snapshot must survive subsequent mutation
   }
   ```
   `ComputeDeviationStats` is a new private static helper. Use a `BinaryHeap`-free streaming approach for percentiles (sort a copy; cost is `O(n log n)` per call — Hogan's 458k pixels sorts in <50ms, fires once per orientation pass).
3. After the rim-mask build (if applicable), emit `OnRimMask` + `LogTrace`:
   ```csharp
   if (rim == RimMaskMode.DeviationFlood && hooks?.OnRimMask is not null)
   {
       int rimCount = 0, fgInitialCount = 0, fgSurvivorCount = 0;
       for (int i = 0; i < n; i++) { if (rimMask[i]) rimCount++; if (/*fg-before-subtract*/) fgInitialCount++; }
       // Note: fg has been mutated by this point; use a saved copy or count before mutating.
   }
   ```
   Easier: keep `fg-initial` as a separate `bool[]` so its count is stable. Compute the rim-subtract pass-by-pass.
4. After the morph close (if `closeRadius > 0`), emit `OnMorph` + `LogTrace` with the `fg-after-morph` clone.
5. In the per-comp `foreach` loop, after `Classify` returns, emit `OnBlobClassified` for ALL comps + `LogTrace`. Use the existing `f.Pixels` list pass-through for `BlobClassification.Pixels`.
6. **Critical clone semantic**: every `bool[]` (and `float[]`) handed to a snapshot is a `.Clone()` of the orchestrator's working buffer — because the orchestrator continues to mutate `fg` in subsequent stages (rim subtract, morph close). Without cloning, the snapshot's `ForegroundBuffer` would silently change to the post-rim or post-morph state by the time the engine assigns it to the context. Test for this exact regression in step Tests.

**Tests:** In `DeviationBlobCalibrationDetectorTests.cs` (the project already has the `BuildPair` fixture — re-use it):

- `Detector_output_is_identical_with_and_without_hooks` (analog of #1122's `Detector_output_is_identical_with_and_without_sink`):
  ```csharp
  var (shot, tex) = BuildPair();
  var detector = new DeviationBlobCalibrationDetector();
  var baseRequest = Request(shot, tex);
  var withoutHooks = detector.Detect(baseRequest);
  var hooks = new DetectionDiagnosticHooks(
      OnDeviation: _ => { }, OnRimMask: _ => { }, OnMorph: _ => { }, OnBlobClassified: _ => { });
  var withHooks = detector.Detect(baseRequest with { Diagnostics = hooks });
  withHooks.Keys.Should().BeEquivalentTo(withoutHooks.Keys);
  foreach (var key in withoutHooks.Keys)
      withHooks[key].Should().BeEquivalentTo(withoutHooks[key]);
  ```
- `OnDeviation_fires_with_foreground_buffer_matching_above_threshold_count` — drive `Detect`; assert exactly one `DeviationSnapshot` per `Detect` invocation (this test goes through `Detect`, not the engine — single orientation). Assert `ForegroundBuffer.Length == W*H` and `AboveThresholdCount == ForegroundBuffer.Count(b => b)`.
- `OnRimMask_fires_with_blob_detection_pipeline_tag` — assert exactly one `RimMaskSnapshot` with `Pipeline == "blob_detection"`. Assert `RimMaskBuffer.Length == W*H`.
- `OnMorph_fires_with_fgInputCount_matching_rim_survivor_count` — assert `MorphSnapshot.FgInputCount == RimMaskSnapshot.FgSurvivorCount`.
- `OnBlobClassified_fires_for_all_comps_not_just_Icons` — drive with a fixture that produces Noise + Icon (might need to extend `BuildPair` to include a too-small fragment). Assert at least one record with `BlobClass == "Noise"` and at least one with `BlobClass == "Icon"`.
- `Snapshots_buffers_are_clones_not_references` — wire a sink that captures the snapshot, then drive a SECOND `Detect` call. Assert the first snapshot's `ForegroundBuffer` is unchanged (proves the clone happened, not a view).

**Acceptance:** All new tests green. `Detector_output_is_identical_with_and_without_hooks` is the backward-compat lock per D8.

---

## Task 3 — `DeviationBlobCalibrationDetector.Detect` threads hooks + `meanNcc`

**Files:** [`src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs).

**Steps:**

1. Capture the `meanNcc` `out` param from `LocalNccDeviation.DeviationMap` (today at line 53: `out _` — change to `out var meanNcc`).
2. Thread `request.Diagnostics` + `meanNcc` into the `DetectIconBlobs` call:
   ```csharp
   var blobs = DeviationBlobDetector.DetectIconBlobs(
       dev, w, h, request.LowNcc, rim, request.BlobOptions, closeRadius: 1,
       hooks: request.Diagnostics,
       meanNcc: meanNcc);
   ```
3. **Add LogTrace mirror for `DetectIconBlobs`'s emissions when `_logger != null`.** Since the static `DetectIconBlobs` doesn't have an `ILogger`, the per-stage trace lines fire from the detector class (which has `_logger` after #1122). Pattern: extend the hooks aggregate at the call site with a Trace-mirror lambda that fires alongside the real sink. Or — simpler — have `DetectIconBlobs` emit the LogTrace directly when `hooks != null`, by passing the `_logger` field as a separate parameter.

   **Resolved approach**: pass `ILogger? logger` as a new parameter to `DetectIconBlobs`. Same shape as the existing `DeviationBlobCalibrationDetector(ILogger? logger)` ctor from #1122. The static helper takes both `hooks` and `logger`; when `hooks != null` it formats + logs. This keeps the trace mirror tightly coupled to sink emission (one if-branch covers both) and matches #1121's prior art at `DeviationBlobCalibrationDetector.EmitDiagnostic`.

   Updated `DetectIconBlobs` signature:
   ```csharp
   public static IReadOnlyList<BlobFeat> DetectIconBlobs(
       float[] dev, int w, int h, double lowNcc, RimMaskMode rim, BlobOptions opts, int closeRadius,
       DetectionDiagnosticHooks? hooks = null,
       double meanNcc = double.NaN,
       ILogger? logger = null)
   ```
   This is the FINAL signature — Task 2 starts with the simpler 8-arg version; Task 3 adds `logger`. Or fold it into Task 2 (acceptable; the spec just sequences the conceptual layers, the implementer can collapse).

**Tests:** Update Task 2's tests to confirm the trace mirror — pass a `FakeLogger` (or `Mock<ILogger>` — whichever convention the test project uses; check via `cat tests/Mithril.MapCalibration.Tests/Detection/DeviationBlobCalibrationDetectorTests.cs` for fixture pattern). Assert one `LogTrace` record per stage + per-blob.

**Acceptance:** `_logger` is genuinely used. `Diagnostic_sink_fires_per_blob_per_template_when_wired` (#1122's existing test) continues to pass.

---

## Task 4 — `IconLikelihoodField.LoadDeviationAsField` 3-arg overload

**Files:** [`src/Mithril.MapCalibration.Detection/IconLikelihoodField.cs`](../../../src/Mithril.MapCalibration.Detection/IconLikelihoodField.cs).

**Steps:**

0. **Re-grep** `LoadDeviationAsField` across `src/`, `tools/`, `tests/` to confirm no caller besides the documented ones (verification owed in spec §11 row 3). Expected: `MapCalibrationSolveEngine.cs:493`, `tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs:244`, and the test files.
1. Add the new overload:
   ```csharp
   /// <summary>
   /// Overload with a caller-supplied pre-built rim mask. Used by the synthesis-J
   /// orchestrator (mithril#1123) which now lifts rim-mask computation out of the
   /// per-template loop into the orchestrator body, computing it once per orientation
   /// and emitting the OnRimMask diagnostic. The other overloads delegate to this one
   /// internally (after building their own mask) so behaviour is identical.
   /// </summary>
   public static double[,] LoadDeviationAsField(
       GrayImage deviation, IconTemplate template, bool[] rim)
   {
       int n = deviation.Width * deviation.Height;
       if (rim.Length != n)
           throw new ArgumentException($"rim.Length ({rim.Length}) must equal deviation.Width*Height ({n}).", nameof(rim));
       var maskedPixels = new byte[n];
       for (int i = 0; i < n; i++) maskedPixels[i] = rim[i] ? (byte)0 : deviation.Pixels[i];
       var masked = new GrayImage(deviation.Width, deviation.Height, maskedPixels);
       return ScoreAll(masked, template);
   }
   ```
2. **Refactor the existing 4-arg `(deviation, template, applyRimMask, devThr)` overload** at lines 61-77 to delegate to the new 3-arg overload when `applyRimMask` is true:
   ```csharp
   public static double[,] LoadDeviationAsField(
       GrayImage deviation, IconTemplate template, bool applyRimMask, double devThr)
   {
       if (!applyRimMask) return ScoreAll(deviation, template);
       int n = deviation.Width * deviation.Height;
       var dev = new float[n];
       for (int i = 0; i < n; i++) dev[i] = deviation.Pixels[i] / 255f;
       var rim = DeviationFloodRimMask.Build(dev, deviation.Width, deviation.Height, devThr);
       return LoadDeviationAsField(deviation, template, rim);   // delegate
   }
   ```
   This proves byte-equivalence by construction: 4-arg and 3-arg now share the same ScoreAll-on-masked-deviation tail.
3. The convenience 2-arg overload at lines 54-55 is unchanged (still delegates to 4-arg).

**Tests:** Create new [`tests/Mithril.MapCalibration.Tests/Detection/IconLikelihoodFieldOverloadTests.cs`](../../../tests/Mithril.MapCalibration.Tests/Detection/):

- `LoadDeviationAsField_3arg_matches_4arg_when_rim_is_freshly_built` — build deviation, compute rim via `DeviationFloodRimMask.Build`, call both overloads, assert byte-equal `double[,]` field.
- `LoadDeviationAsField_3arg_throws_when_rim_length_mismatched` — pass a 100-element rim with a 200-pixel image; expect `ArgumentException`.

**Acceptance:** All existing `SynthesisRerankFieldEquivalenceTests` continue to pass (proves the 4-arg overload's behaviour is preserved). New tests green.

---

## Task 5 — `BuildLikelihoodFieldsFromDeviation` non-static + hooks + lifted rim

**Files:** [`src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs), [`tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs`](../../../tests/Mithril.MapCalibration.Tests/Detection/SynthesisRerankFieldEquivalenceTests.cs).

**Steps:**

0. **Re-grep** `BuildLikelihoodFieldsFromDeviation` across `tests/` to confirm the one external caller (`SynthesisRerankFieldEquivalenceTests.cs:107`) — verification owed in spec §11 row 2.
1. Flip `internal static` → `private` on `BuildLikelihoodFieldsFromDeviation` at line 450 (it's called from `Solve` at line 103 — within the same class, so private works).
2. **Extend signature with `rotate180` + `hooks` parameters**:
   ```csharp
   private IReadOnlyDictionary<string, double[,]> BuildLikelihoodFieldsFromDeviation(
       GrayImage screenshot, GrayImage baseTexture, IconTemplateSet templates,
       double typeFloor, int? renderSizePx,
       bool rotate180,
       DetectionDiagnosticHooks? hooks)
   ```
3. **Lift rim-mask out of the per-template loop** per spec §5.5: after building the additive deviation `devImage` (the existing code at lines 460-467), convert to `float[]`, build the rim via `DeviationFloodRimMask.Build` ONCE, emit `OnRimMask` if hooks wired:
   ```csharp
   int w = screenshot.Width, h = screenshot.Height;
   int n = w * h;
   // … existing additive-deviation byte[] build (lines 460-467) …
   var devThr = IconLikelihoodField.DefaultDevThr;
   var devAsFloat = new float[n];
   for (int i = 0; i < n; i++) devAsFloat[i] = devImage.Pixels[i] / 255f;
   var rim = DeviationFloodRimMask.Build(devAsFloat, w, h, devThr);

   if (hooks?.OnRimMask is not null)
   {
       int rimCount = 0;
       for (int i = 0; i < n; i++) if (rim[i]) rimCount++;
       hooks.OnRimMask(new RimMaskSnapshot(
           Pipeline: "synthesis_j",
           Rotate180: rotate180,
           Width: w, Height: h,
           Threshold: devThr,
           RimPixelCount: rimCount,
           FgInputCount: -1, FgSurvivorCount: -1,
           RimMaskBuffer: (bool[])rim.Clone()));
       _logger?.LogTrace(
           "RimMask (rotate180={Rotate180}, pipeline=synthesis_j): rim={Rim} of {N} px (threshold={T:0.000}).",
           rotate180, rimCount, n, devThr);
   }
   ```
4. **Per-template loop**: replace the existing `LoadDeviationAsField(devImage, template, applyRimMask: true, devThr: DefaultDevThr)` call (line 493) with `LoadDeviationAsField(devImage, template, rim)` — uses the new 3-arg overload from Task 4.
5. **Update the call site in `Solve`** (line 103-105) to pass `rotate180` + `request.Diagnostics`:
   ```csharp
   var fields = BuildLikelihoodFieldsFromDeviation(
       req.Screenshot, req.BaseTexture, req.Templates,
       req.TypeFloor, req.RenderSizePx,
       rotate180,
       req.Diagnostics);
   ```
6. **Update `SynthesisRerankFieldEquivalenceTests.cs:107`**: it currently calls `MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation(...)` as a static. After Task 5 the method is `private` on an instance. Options: (a) make it `internal` (and the test project already has `InternalsVisibleTo`), call as `engine.BuildLikelihoodFieldsFromDeviation(...)`; (b) expose a test seam. Pick (a) — flip `private` → `internal`, construct an engine fixture in the test (engine already has zero-cost ctor in test paths with a stub detector + gate). Confirm `InternalsVisibleTo` via `cat src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj`.

**Tests:** Create new [`tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineSynthesisRimSinkTests.cs`](../../../tests/Mithril.MapCalibration.Tests/Detection/):

```csharp
[Fact]
public void BuildLikelihoodFieldsFromDeviation_emits_synthesis_j_pipeline_tag()
{
    var (shot, tex) = BuildPair();
    var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);
    var rimSnaps = new List<RimMaskSnapshot>();
    var hooks = new DetectionDiagnosticHooks(
        OnDeviation: null, OnRimMask: rimSnaps.Add, OnMorph: null, OnBlobClassified: null);

    var engine = new MapCalibrationSolveEngine(
        detector: new DeviationBlobCalibrationDetector(),
        gate: new CalibrationConfidenceGate(),
        logger: null,
        options: new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Shadow });

    // Drive via the public Solve API so the orientation wrap also exercises:
    var refs = SyntheticMap.BuildReferences();
    var req = new DetectionRequest(shot, tex, /* …other args… */) { Diagnostics = hooks };
    engine.Solve(req, refs);

    rimSnaps.Should().NotBeEmpty();
    rimSnaps.Should().AllSatisfy(s => s.Pipeline.Should().Be("synthesis_j"));
    rimSnaps.Should().AllSatisfy(s => s.RimMaskBuffer.Length.Should().Be(shot.Width * shot.Height));
}
```

Plus update `SynthesisRerankFieldEquivalenceTests.cs:107` to construct + call via instance.

**Acceptance:** New test green. `SynthesisRerankFieldEquivalenceTests` continue to pass (rim-mask-lift-out is byte-equivalent — Task 4's 4-arg overload still produces the same field). `dotnet test` clean.

---

## Task 6 — `MapCalibrationSolveEngine.Solve` extends Rotate180 wrap to all four sinks

**Files:** [`src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs).

**Steps:**

1. At the top of the `foreach (var rotate180 in new[] { false, true })` body (currently line 51-63), build the local-copy hoists + the wrapped hooks per spec §5.4:
   ```csharp
   var orientationFlag = rotate180;
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
   ```
2. Pass `wrappedHooks` to the `request with { … Diagnostics = wrappedHooks }` builder (line 63 area). The existing `BlobScoreSink` wrap stays exactly as it is.
3. Also pass `rotate180` to `BuildLikelihoodFieldsFromDeviation` (from Task 5) — same wrap idiom applies to the synth-J rim emission since the call happens inside the orientation loop with the flag in scope.

**Tests:** Extend the Task 5 test:

```csharp
[Fact]
public void Solve_wraps_diagnostic_hooks_with_orientation_flag()
{
    var deviationSnaps = new List<DeviationSnapshot>();
    var hooks = new DetectionDiagnosticHooks(
        OnDeviation: deviationSnaps.Add, OnRimMask: null, OnMorph: null, OnBlobClassified: null);
    var req = /* … */ with { Diagnostics = hooks };
    engine.Solve(req, refs);

    deviationSnaps.Should().HaveCount(2);
    deviationSnaps.Should().Contain(s => s.Rotate180 == false);
    deviationSnaps.Should().Contain(s => s.Rotate180 == true);
}
```

**Acceptance:** Test green. Detector itself still emits `Rotate180 = false` on every record (the wrapper does the rewrite). `Detector_output_is_identical_with_and_without_hooks` continues to pass.

---

## Task 7 — `CalibrationAttemptContext` + `AttemptFilesJson` + `BlobPipelineJson` DTOs

**Files:** [`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs), [`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs).

**Steps:**

1. In `CalibrationAttemptContext.cs`, add four nullable list properties after the existing `BlobTemplateScores` (line 50-57):
   ```csharp
   /// <summary>Per-orientation deviation stats + fg-initial bool[] (mithril#1123).</summary>
   public IReadOnlyList<DeviationSnapshot>? DeviationSnapshots { get; set; }
   /// <summary>Per-(orientation, pipeline) rim mask bool[] (mithril#1123). pipeline ∈ {"blob_detection","synthesis_j"}.</summary>
   public IReadOnlyList<RimMaskSnapshot>? RimMaskSnapshots { get; set; }
   /// <summary>Per-orientation morph-close output bool[] (mithril#1123).</summary>
   public IReadOnlyList<MorphSnapshot>? MorphSnapshots { get; set; }
   /// <summary>Per-blob (across all comps, not just Icons) classification + pixel list (mithril#1123).</summary>
   public IReadOnlyList<BlobClassification>? BlobClassifications { get; set; }
   ```
2. In `CalibrationBundleJson.cs`, extend `AttemptFilesJson` with 11 new optional string slots after `BlobTemplateScores = null`:
   ```csharp
   string? BlobPipeline = null,           // 10c-blob-pipeline.json
   string? Foreground = null,             // 07b-foreground.png
   string? ForegroundR180 = null,
   string? RimMask = null,                // 07c-rim-mask.png
   string? RimMaskR180 = null,
   string? SynthRimMask = null,           // 07c-synth-rim-mask.png
   string? SynthRimMaskR180 = null,
   string? Morphed = null,                // 07d-morphed.png
   string? MorphedR180 = null,
   string? BlobClassification = null,     // 07e-blob-classification.png
   string? BlobClassificationR180 = null
   ```
3. Add the `BlobPipelineJson` DTOs:
   ```csharp
   public sealed record DeviationSectionJson(
       bool Rotate180, int Width, int Height, int Win,
       double Threshold, double MeanNcc,
       double Min, double Max, double Mean,
       double P50, double P95, double P99,
       int AboveThresholdCount);

   public sealed record RimMaskSectionJson(
       string Pipeline, bool Rotate180, int Width, int Height,
       double Threshold, int RimPixelCount, int FgInputCount, int FgSurvivorCount);

   public sealed record MorphSectionJson(
       bool Rotate180, int Width, int Height,
       int CloseRadius, int FgInputCount, int FgOutputCount);

   public sealed record BlobJson(
       bool Rotate180, int BlobOrdinal,
       int MinX, int MinY, int W, int H, int Area,
       double Cx, double Cy,
       double MeanDev, double PeakDev,
       double Solidity, double Aspect,
       string BlobClass);

   public sealed record BlobPipelineJson(
       int SchemaVersion,
       IReadOnlyList<DeviationSectionJson> Deviation,
       IReadOnlyList<RimMaskSectionJson> RimMasks,
       IReadOnlyList<MorphSectionJson> Morph,
       IReadOnlyList<BlobJson> Blobs);
   ```
   Note: `BlobJson` does NOT carry `Pixels` — that's a render-only payload retained on the context-side `BlobClassification` record but NOT serialised to JSON (per spec §5.1 doc).
4. Register `[JsonSerializable(typeof(BlobPipelineJson))]` on `CalibrationBundleJsonContext`.

**Tests:** None for the type extensions alone — Task 8 covers serialisation round-trip. Compile-green is the assertion.

**Acceptance:** Build green. Pre-#1123 readers of `AttemptFilesJson` round-trip unchanged because new fields default-null.

---

## Task 8 — `FilesystemCalibrationAttemptBundleSink` writes 10c + 10 PNGs

**Files:** [`src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs).

**Steps:**

1. **Two new helper methods** for bool[] → PNG and label-map → PNG:
   ```csharp
   private string? TryWriteBoolMaskPng(string dir, string name, int w, int h, bool[] mask)
   {
       try
       {
           var bytes = new byte[w * h];
           for (int i = 0; i < bytes.Length; i++) bytes[i] = mask[i] ? (byte)255 : (byte)0;
           var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, bytes, w);
           return WritePng(dir, name, src);
       }
       catch (Exception ex) { _logger?.LogWarning(ex, "{Name} write failed", name); return null; }
   }

   private string? TryWriteBlobClassificationPng(string dir, string name,
       int w, int h, IEnumerable<BlobClassification> classifications)
   {
       try
       {
           var bgra = new byte[w * h * 4];   // black-fill default
           foreach (var c in classifications)
           {
               var (b, g, r) = c.BlobClass switch
               {
                   "Icon"      => ((byte)0,   (byte)200, (byte)0),
                   "Fog"       => ((byte)200, (byte)100, (byte)40),
                   "Structure" => ((byte)0,   (byte)0,   (byte)200),
                   "Noise"     => ((byte)80,  (byte)80,  (byte)80),
                   _           => ((byte)0,   (byte)0,   (byte)0),
               };
               foreach (var pixIdx in c.Pixels)
               {
                   int ofs = pixIdx * 4;
                   bgra[ofs] = b; bgra[ofs + 1] = g; bgra[ofs + 2] = r; bgra[ofs + 3] = 255;
               }
           }
           var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
           return WritePng(dir, name, src);
       }
       catch (Exception ex) { _logger?.LogWarning(ex, "{Name} write failed", name); return null; }
   }
   ```
2. **`TryWriteBlobPipelineJson`** — maps context's four lists to `BlobPipelineJson` DTO. Omit when ALL four context lists are null/empty (same null-or-empty pattern as #1121's `TryWriteBlobTemplateScoresJson`). When emitting, drop `Pixels` from `BlobClassification` → `BlobJson` (render-only payload, JSON gets the rest).
3. **`TryWriteForegroundPng`, `TryWriteRimMaskPng` (×2 pipelines), `TryWriteMorphedPng`, `TryWriteBlobClassificationPng`** — each takes the matching snapshot from the context, slices by rotate180, calls `TryWriteBoolMaskPng` / `TryWriteBlobClassificationPng` with the right filename + buffer. Pattern:
   ```csharp
   private string? TryWriteForegroundPng(string dir, CalibrationAttemptContext ctx, bool rotate180)
   {
       var snap = ctx.DeviationSnapshots?.FirstOrDefault(s => s.Rotate180 == rotate180);
       if (snap is null) return null;
       var name = rotate180 ? "07b-r180-foreground.png" : "07b-foreground.png";
       return TryWriteBoolMaskPng(dir, name, snap.Width, snap.Height, snap.ForegroundBuffer);
   }
   ```
   Repeat the shape for rim (with pipeline filter), morph, classification.
4. **Wire all 11 new file slots into `AttemptFilesJson` construction** in `Write` (around line 65-76):
   ```csharp
   var files = new AttemptFilesJson(
       /* existing 10 positional args + BlobTemplateScores */,
       BlobTemplateScores: TryWriteBlobTemplateScoresJson(subdir, context),
       BlobPipeline: TryWriteBlobPipelineJson(subdir, context),
       Foreground:           TryWriteForegroundPng         (subdir, context, rotate180: false),
       ForegroundR180:       TryWriteForegroundPng         (subdir, context, rotate180: true),
       RimMask:              TryWriteRimMaskPng            (subdir, context, "blob_detection", rotate180: false),
       RimMaskR180:          TryWriteRimMaskPng            (subdir, context, "blob_detection", rotate180: true),
       SynthRimMask:         TryWriteRimMaskPng            (subdir, context, "synthesis_j",    rotate180: false),
       SynthRimMaskR180:     TryWriteRimMaskPng            (subdir, context, "synthesis_j",    rotate180: true),
       Morphed:              TryWriteMorphedPng            (subdir, context, rotate180: false),
       MorphedR180:          TryWriteMorphedPng            (subdir, context, rotate180: true),
       BlobClassification:   TryWriteBlobClassificationPng (subdir, context, rotate180: false),
       BlobClassificationR180: TryWriteBlobClassificationPng(subdir, context, rotate180: true));
   ```

**Tests:** Extend [`tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs`](../../../tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs):

- `Writes_blob_pipeline_json_and_pngs_when_context_populated` — manually populate `DeviationSnapshots`, `RimMaskSnapshots` (both pipelines × both orientations), `MorphSnapshots`, `BlobClassifications` on a fake context; assert all 11 file slots are non-null in the round-tripped `01-attempt.json`; assert `10c-blob-pipeline.json` deserialises with `SchemaVersion == 1` + at least one record per section; assert the PNG files exist on disk.
- `Omits_blob_pipeline_when_context_null` — null context → all 11 slots null.
- `Omits_blob_pipeline_when_lists_empty` — empty (length-zero) context lists → all 11 slots null (matches #1121's "empty → omit" convention).
- `BlobClassification_png_paints_pixels_per_blob_class` — wire a context with one Icon-class blob covering a 3×3 region; render PNG; load back; assert the 9 pixels are green and the rest are black.

**Acceptance:** All new tests green. #1121's existing `Writes_blobTemplateScores_when_context_populated` and `Omits_blobTemplateScores_when_context_null_or_empty` continue to pass (with the Task 0 BlobOrdinal rename applied).

---

## Task 9 — `AutoCalibrationEngine.RunAttemptCoreAsync` wires the hooks

**Files:** [`src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs).

**Steps:**

1. At lines 660-705 (the #1121 wiring zone), extend the BlobScoreSink wiring with four new lists + a `DetectionDiagnosticHooks` aggregate:
   ```csharp
   // mithril#1123: collect per-stage deviation/rim/morph/classification observations
   // for the diagnostic bundle. Engine layer creates the buffers; the detector layer
   // (via the SolveEngine's rotate180 wrapper) appends one record per (stage, orientation).
   // null sink at any field → zero producer cost for that stage.
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
   ```
2. After the `Solve` call returns (lines ~696-703), assign all four lists to the context (alongside the existing `attempt.BlobTemplateScores = blobScores;`):
   ```csharp
   attempt.BlobTemplateScores  = blobScores;
   attempt.DeviationSnapshots  = deviationSnaps;
   attempt.RimMaskSnapshots    = rimSnaps;
   attempt.MorphSnapshots      = morphSnaps;
   attempt.BlobClassifications = blobClasses;
   ```
   Always assigned (even when empty) so the bundle sink distinguishes "diagnostic wiring missing" from "diagnostic ran, found nothing."

**Tests:** No direct unit test — the wiring is integration-shape (engine + solve + detector + bundle sink). Verification path is in Task 11.

**Acceptance:** Build green. Pre-#1123 paths through the engine still work (the new lists are additive, never read by anything besides the new bundle-sink writers).

---

## Task 10 — `INDEX.md` row + (optional) `docs/perf-trace-schema.md`

**Files:** [`docs/planning/INDEX.md`](../INDEX.md), optionally [`docs/perf-trace-schema.md`](../../perf-trace-schema.md).

**Steps:**

1. Append the row to `docs/planning/INDEX.md`:
   ```
   | [calibration-pipeline-observability-1123](calibration-pipeline-observability-1123/) | active | [#1123](https://github.com/moumantai-gg/mithril/issues/1123) | Static-utility decision owners in detector pipeline (deviation, rim mask, blob classify) — fills the mithril#1093 + mithril#1121 audit gap |
   ```
   Place alphabetically (after `calibration-logging-pass-1093`, before `calibration-1095-live-view-detector`).
2. **Optional — `docs/perf-trace-schema.md`**: append a one-paragraph note under "What's instrumented today" mentioning the new LogTrace mirror cadence (per-stage stats + per-blob Classify in `Mithril.MapCalibration.Detection`). Skip if it'd duplicate existing wording.

**Tests:** None. Spec/plan docs.

**Acceptance:** `INDEX.md` row visible; markdown links resolve.

---

## Task 11 — Verification

**Files:** None code-side. Manual + smoke test.

**Steps:**

1. **`dotnet build Mithril.slnx`** — full clean build green.
2. **`dotnet test Mithril.slnx`** — all tests green. Particularly:
   - `Detector_output_is_identical_with_and_without_hooks` (Task 2) — backward-compat lock.
   - `SynthesisRerankFieldEquivalenceTests.Production_path_and_probe_LoadDeviationAsField_produce_identical_fields` (existing) — proves Task 5's rim-mask-lift-out is byte-equivalent.
   - `BlobOrdinal_is_set_by_ConnectedComponents_Label_emission_order` (Task 0) — proves the unified ordinal space.
3. **Manual smoke**: launch Mithril against the Hogan's Basement screenshot via the manual-trigger hotkey. Confirm:
   - The bundle subdirectory under `%LocalAppData%/Mithril/diagnostics/calibration/` contains `10c-blob-pipeline.json` + 10 new PNG files (`07b-*.png`, `07c-*.png` (×2 pipelines), `07d-*.png`, `07e-*.png`) for both orientations.
   - `10c-blob-pipeline.json` has `deviation: [2 entries]`, `rimMasks: [4 entries]` (×2 orientation × 2 pipeline), `morph: [2 entries]`, `blobs: [~50 entries]` (all classifications, not just Icons).
   - `01-attempt.json.Files.BlobPipeline == "10c-blob-pipeline.json"`.
   - `10b-blob-template-scores.json.SchemaVersion == 2`; the `BlobOrdinal` value in `10b` cross-refs to the `BlobOrdinal` value in `10c.blobs[]` (the matching record has `BlobClass == "Icon"`).
   - `mithril-<date>.json` Serilog log shows the new LogTrace lines from `Mithril.MapCalibration.Detection` category (per-stage + per-blob).
4. **Memory budget smoke**: trigger 3 back-to-back calibration attempts on Hogan's via the hotkey. Confirm `Mithril` working set in Task Manager doesn't grow unboundedly (~4 MB transient per attempt, all GC-eligible after bundle write).
5. **The NPC pip recall diagnostic** (the original use case): open `07e-blob-classification.png` in an image viewer. Locate the NPC pip cluster region (X 220-290, Y 90-170 in cropped-frame coords). Read off which `BlobClass` color covers that region. Cross-reference to `10c.blobs[]` by bbox to read the exact `(meanDev, peakDev, solidity, aspect, blobClass)` values. The Hogan's #1116 investigation can then pick the correct fix arm (rim-mask leakage / sub-pixel locator drift / `BlobOptions` rejection) instead of speculating.

**Acceptance:** All four numbered acceptance criteria above hold. Manual smoke produces actionable triage data for #1116 — this task is the load-bearing demonstration that #1123 unblocks #1116.

---

## Out of scope (deferred to follow-up issues if needed)

- **`07-deviation.png` rendering migration** — today's bundle sink computes the deviation map a SECOND time inside the visualizer (`_visualizer.RenderDeviation(AlignedCrop, AlignedTexture)` at line 175). After #1123 the detector's `DeviationSnapshot` carries the threshold-output `bool[]` but NOT the underlying `float[] dev`. Migrating `07-deviation.png` to reuse `DeviationSnapshot`'s data would require carrying the float buffer too (~1.8 MB × 2 retention), or a different shape entirely. Out of scope; a follow-up issue can decide.
- **Per-pixel observability hooks inside the static utilities themselves** — would shred the perf budget. Out of scope per spec D7.
- **Whole-image fallback detector observability** — different path, separate audit per spec §2 / §10.
- **Synthesis-J `OnDeviation` / `OnMorph` / `OnBlobClassified`** — those stages don't exist in synth-j; their absence is by design, not omission. Per spec D6.

---

## Commit cadence

Eleven tasks → eleven commits. Per `collaboration_style` user memory ("frequent commits"). Each commit message uses the `feat(map-calibration): … (mithril#1123)` shape matching recent #1121 / #1117 commits. PR title: `feat(map-calibration): detector-pipeline observability — DeviationSnapshot + RimMaskSnapshot + MorphSnapshot + BlobClassification sinks (closes #1123)`.
