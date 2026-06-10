# Map auto-calibration: blur-aware Sobel template — plan

**Spec:** [`spec.md`](spec.md). **Issue:** [mithril#1070](https://github.com/moumantai-gg/mithril/issues/1070). **Branch posture:** docs-only spec+plan PR (this commit); implementation lands on a fresh feature branch the next session creates.

Seven tasks. Ordered so each commit reads independently. **Task 0 is the σ-curve measurement**, ground for every default downstream. Tasks 1–3 build the type vocabulary + the production code path. Tasks 4–6 are tests + persistence + docs + telemetry. Task 7 is end-to-end verification.

The plan follows the TDD-where-possible pattern that prior calibration specs (#1061, #1123) used: vocabulary types first (no behaviour, build green), then producers (with the unit tests that pin them), then orchestration (with the integration tests).

---

## Task 0 — Measurement experiment: fit σ(scale)

**Files:** Throwaway harness under `tools/MapCalibrationFromScreenshot/BlurFitSpike/` — analogous to #1061's `SparseLocateSpike.cs`, deleted when the implementation lands.

**Goal:** Produce four numbers (`RendererBlurIntercept`, `RendererBlurSlope`, `RendererBlurMinSigma`, `RendererBlurMaxSigma`) that go into `MapCalibrationLocateOptions`'s constructor defaults. Plus a fit-residuals number that gates whether the linear model is good enough (D3 → piecewise fallback).

**Steps:**

1. **Corpus collection** — the user captures Hogan's Basement at 4–5 well-separated zoom levels via the manual hotkey + matching outdoor Eltibule control. Three Hogan's are already in hand (`Map_HogansKeepBasement-…091533/154134/154213`). Two additional mid-zoom Hogan's would shore up the curve fit; collect at scale ~0.50 and ~0.65 if possible. (Less ideal: fit on the existing three points; the linear model only needs two but residual quality drops.)
2. **Per-bundle autocorrelation-width measurement.** For each bundle:
   - Load `06-aligned-screenshot.png` (cropped to the locator's recovered region).
   - Run Sobel magnitude on the cropped screenshot. Compute its 2D autocorrelation function, measure half-max width (in pixels).
   - Load `05-base-texture-resampled.png` (the template, already resized to the recovered scale). Run Sobel magnitude on it. Compute its 2D autocorrelation half-max width.
   - σ_needed = `sqrt(max(0, screenshot_width² - template_width²) / 8 ln 2)` — the σ of a Gaussian that, convolved with the template, makes its autocorrelation match the screenshot's. (Standard Gaussian-convolution-of-Gaussian width arithmetic.)
3. **Fit + residual check.** Linear regression of σ_needed vs `1/scale`. Compute slope, intercept, max residual. If max residual / mean(σ_needed) > 0.10, the linear model under-fits — capture the per-point data + fall through to D3's piecewise alternative at Task 1 step 3 (no production change, just a different default-init shape).
4. **Settle the clamps.** `MinSigma = 0.0` is fine (no blur for the no-mismatch case). `MaxSigma = 3.0` is a defensive ceiling (ksize ≈ 19 px — never exceeded by realistic data, but the clamp prevents a future bad-fit pathology).
5. **Document the fitted numbers** in this plan file (replace the placeholders below) AND update `spec.md` §5.3's PLACEHOLDER comments AND update `RendererBlurModelTests.cs::SigmaFor_is_linear_in_inverse_scale`'s expected values to match.

**Tests:** None (throwaway spike).

**Acceptance:** Four production-ready defaults (with provenance — the corpus they were fit on, the residual quality), recorded in this plan + the spec + the unit test.

**Status placeholder (replace with measured values when Task 0 lands):**

```text
RendererBlurIntercept = TBD (fit-measured)
RendererBlurSlope     = TBD (fit-measured)
RendererBlurMinSigma  = 0.0
RendererBlurMaxSigma  = 3.0
Fit residual max      = TBD < 0.10 × mean(σ_needed)?
Corpus                = TBD bundles: …list…
```

---

## Task 1 — `MapCalibrationLocateOptions` v1→v2

**Files:** [`src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptions.cs`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptions.cs).

**Steps:**

1. Bump `public const int Version = 1` → `2`.
2. Add five new backing fields + properties (defaults are the Task 0 measured values — until Task 0 lands, the spec's placeholders 0.10/0.20/0.0/3.0 are stand-ins):
   ```csharp
   private bool   _rendererBlurEnabled   = true;
   private double _rendererBlurIntercept = 0.10;
   private double _rendererBlurSlope     = 0.20;
   private double _rendererBlurMinSigma  = 0.0;
   private double _rendererBlurMaxSigma  = 3.0;

   /// <summary>
   /// Apply renderer-blur-aware matched Gaussian to the Sobel template at the
   /// fallback's full-resolution stage. Default true (mithril#1070). Off
   /// disables the blur path and forces 0.0 σ — a regression to pre-#1070
   /// behaviour for the same fallback.
   /// </summary>
   public bool RendererBlurEnabled
   {
       get => _rendererBlurEnabled;
       set { if (_rendererBlurEnabled != value) { _rendererBlurEnabled = value; OnChanged(); } }
   }

   // …Intercept, Slope, MinSigma, MaxSigma in the same pattern…
   ```
3. (If Task 0's fit was poor.) Add a sixth property — `RendererBlurSigmaTable : double[]?` — that overrides the linear model when non-null. `RendererBlurModel.SigmaFor` checks the table first (piecewise-linear interpolation between rungs), falls through to the linear model. Skip this property if Task 0's fit residuals are good.
4. Update `Migrate`:
   ```csharp
   public static MapCalibrationLocateOptions Migrate(MapCalibrationLocateOptions loaded)
   {
       if (loaded.SchemaVersion >= Version) return loaded;
       // v1 → v2: additive — RendererBlur* properties initialise from constructor
       // defaults (the Plan Task 0 measured fit). No field is renamed or dropped.
       return loaded;
   }
   ```
   (The Migrate body is identical to v1's; the only semantic difference is that `loaded.SchemaVersion = 1` instances now get the new properties pre-initialised. The settings-store loader writes `SchemaVersion = 2` back after Migrate returns.)

**Tests:**

- **New** `tests/Mithril.MapCalibration.Tests/MapCalibrationLocateOptionsV2MigrateTests.cs`:
  ```csharp
  [Fact]
  public void Migrate_v1_to_v2_preserves_existing_knobs_and_default_inits_blur_props()
  {
      var loaded = new MapCalibrationLocateOptions
      {
          SchemaVersion = 1,
          FallbackNccFloor = 0.30,
      };
      var migrated = MapCalibrationLocateOptions.Migrate(loaded);
      migrated.SchemaVersion.Should().Be(1);    // Migrate doesn't bump; loader does
      migrated.FallbackNccFloor.Should().Be(0.30);
      migrated.RendererBlurEnabled.Should().BeTrue();
      migrated.RendererBlurIntercept.Should().Be(/* Task 0 default */);
      migrated.RendererBlurSlope.Should().Be(/* Task 0 default */);
      migrated.RendererBlurMinSigma.Should().Be(0.0);
      migrated.RendererBlurMaxSigma.Should().Be(3.0);
  }
  ```

**Acceptance:** `dotnet build` green. Test green. Pre-#1070 settings files round-trip without value loss.

---

## Task 2 — `RendererBlurModel` static + unit tests

**Files:** **new** `src/Mithril.MapCalibration.Detection/Internal/RendererBlurModel.cs`, **new** `tests/Mithril.MapCalibration.Tests/Detection/RendererBlurModelTests.cs`.

**Steps:**

1. Add `RendererBlurModel.cs` per spec §5.1 verbatim. Internal-access (`internal static`).
2. Add `RendererBlurModelTests.cs` with the four tests from spec §8:
   - `SigmaFor_returns_zero_when_disabled`
   - `SigmaFor_is_linear_in_inverse_scale` (expected values come from Task 0's fitted defaults — set as test-local constants matching the production defaults)
   - `SigmaFor_clamps_to_min_max`
   - `SigmaFor_returns_min_on_zero_scale`

**Tests:** Listed above.

**Acceptance:** `dotnet test --filter "FullyQualifiedName~RendererBlurModelTests"` green. No production code uses the new type yet — Task 3 wires it in.

---

## Task 3 — `LocateMetrics.BlurAppliedSigma` field

**Files:** the file declaring `LocateMetrics` (verify location: it's referenced from `SobelPaddedPyramidRefiner` per spec — most likely `src/Mithril.MapCalibration.Detection/LocateMetrics.cs` or co-located with `MapRegionRefineResult` in `IMapRegionRefiner.cs`). Confirm via:

```bash
grep -n "record .* LocateMetrics\|class .* LocateMetrics\|struct .* LocateMetrics" src/Mithril.MapCalibration.Detection/*.cs
```

**Steps:**

1. Add `double? BlurAppliedSigma` field to `LocateMetrics`. Default `null` — matches existing nullable-field convention from #1061's `LocateMetrics.Confidence`.
2. **Re-grep `LocateMetrics(` ctor calls** across `src/` + `tests/` to find every construction site. There are likely 1–2 (`FeatureMatchingRefiner` for ORB primary, `SobelPaddedPyramidRefiner` for fallback) plus test fixtures. ORB sites pass `BlurAppliedSigma: null` (blur doesn't apply); the fallback site will be updated in Task 4.

**Tests:** None for the type addition alone — Task 4 covers the producer; Task 5 covers JSON round-trip.

**Acceptance:** `dotnet build` green. No semantic change yet.

---

## Task 4 — `SobelPaddedPyramidRefiner` applies blur + records σ

**Files:** [`src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs`](../../../src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs).

**Steps:**

1. At the FULL-stage loop (post-half-winner narrow ladder), insert the σ-computation + `Cv2.GaussianBlur` call per spec §5.2. Track `bestSigma` alongside `bestNcc / bestScale / bestLoc`.
2. After the loop, plumb `bestSigma` into the returned `LocateMetrics.BlurAppliedSigma` field.
3. Add the three `LogTrace` lines per spec §5.6 — one before the loop (the σ-curve summary), one inside the loop when `σ > 0` is applied (per-rung trace), one after the loop reporting the winner with σ.
4. Add a `LogInformation` line at the top of `RefineCore` reporting whether blur is enabled — once-per-attempt lifecycle milestone.

**Tests:**

- **New** `tests/Mithril.MapCalibration.Tests/Detection/SobelPaddedPyramidRefinerBlurTests.cs`:
  ```csharp
  [Fact]
  public void Refine_records_BlurAppliedSigma_when_enabled()
  {
      var (cap, tex) = BuildSparsePair();    // existing #1061 fixture
      var options = new MapCalibrationLocateOptions { RendererBlurEnabled = true };
      var refiner = new SobelPaddedPyramidRefiner(options);
      var result = refiner.Refine(cap, tex);
      result.Metrics.Should().NotBeNull();
      result.Metrics!.BlurAppliedSigma.Should().NotBeNull();
      result.Metrics.BlurAppliedSigma!.Value.Should().BeGreaterThan(0);
      // …σ matches RendererBlurModel.SigmaFor(metrics.Scale, options)
  }

  [Fact]
  public void Refine_records_zero_sigma_when_disabled()
  {
      var (cap, tex) = BuildSparsePair();
      var options = new MapCalibrationLocateOptions { RendererBlurEnabled = false };
      var refiner = new SobelPaddedPyramidRefiner(options);
      var result = refiner.Refine(cap, tex);
      result.Metrics!.BlurAppliedSigma.Should().Be(0.0);
  }

  [Fact]
  public void Refine_recovers_same_basin_on_sharp_synthetic_with_and_without_blur()
  {
      // Build a synthetic capture+texture pair with no inherent blur.
      // With blur on vs off, recovered (scale, tx, ty) should differ ≤ 0.5 px.
      // Guards against the blur degrading a clean case.
      var (cap, tex) = BuildNoBlurSyntheticPair();
      var on = new SobelPaddedPyramidRefiner(new MapCalibrationLocateOptions { RendererBlurEnabled = true }).Refine(cap, tex);
      var off = new SobelPaddedPyramidRefiner(new MapCalibrationLocateOptions { RendererBlurEnabled = false }).Refine(cap, tex);
      on.AcceptedRect!.OriginX.Should().BeCloseTo(off.AcceptedRect!.OriginX, 1);
      on.AcceptedRect.OriginY.Should().BeCloseTo(off.AcceptedRect.OriginY, 1);
      on.Metrics!.Scale!.Value.Should().BeApproximately(off.Metrics!.Scale!.Value, 0.005);
  }
  ```

**Acceptance:** Three tests green. Existing #1061 round-5 tests continue to pass — the blur path is opt-in via `RendererBlurEnabled`, and (in the FM-disabled corpus the existing tests use) the recovery should be within tolerance.

---

## Task 5 — Corpus tests (Hogan's OUT/IN + Eltibule regression)

**Files:**

- **new** `tests/Mithril.MapCalibration.Tests/Detection/HogansBlurAwareCorpusTests.cs`
- **new** `tests/Mithril.MapCalibration.Tests/Detection/blur_aware_corpus/` directory with checked-in PNGs:
  - `hogans_out_scale028_06-aligned-screenshot.png`
  - `hogans_in_scale094_06-aligned-screenshot.png`
  - `hogans_in_scale094_07b-foreground.png` (expected baseline for the IN-doesn't-regress test)
  - `eltibule_outdoor_02-screenshot-raw.png`
  - `hogans_basement.png` (the bundled base texture, copied from `src/Mithril.MapCalibration/BundledData/`)

**Steps:**

1. Extract the corpus PNGs from `%LocalAppData%/Mithril/diagnostics/calibration/` (or get them from the user — they may need to be sanitised of any session-specific paths if file headers carry them, but PNG itself doesn't embed such metadata so the raw file should be safe to commit as-is). Add `.gitattributes` entry if needed to mark them as binary (`*.png binary`).
2. Add the three corpus tests per spec §8:
   - `Hogans_OUT_corpus_deviation_density_drops_with_blur` — drive the full `Detect(...)` pipeline (refiner → detect blobs → assert `aboveThresholdCount / (W*H) < 0.18`).
   - `Hogans_IN_corpus_already_passing_doesnt_regress` — assert `aboveThresholdCount / (W*H) ≤ 0.15`.
   - `Eltibule_outdoor_doesnt_regress` — drive the full composite refiner (FM primary), assert `Algorithm == "orb-lowe"` (or equivalent — verify the exact contract by reading the existing FM ↔ Sobel discrimination check), assert `BlurAppliedSigma == null`.
3. The tests need to load the bundled texture too. Either copy it to the corpus dir, or load via `BundledBaselineLoader` if that's accessible at test time. Investigate during implementation.

**Tests:** Listed above.

**Acceptance:**

- After this task, the OUT corpus test **fails** (no blur yet → deviation stays at 32 %). That's expected — Task 6 closes the loop.
- IN corpus test should pass already (the existing recovery hits 11 % deviation, well below the 15 % bar; blur should keep it there).
- Eltibule regression should pass already (ORB primary; blur path doesn't run).

The OUT corpus test failing here is the load-bearing TDD signal — after Task 6's measured-σ defaults land, it should flip green.

---

## Task 6 — `LocatorBestJson` schema v2→v3 + bundle wiring

**Files:**

- [`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs)
- [`src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs)

**Steps:**

1. In `CalibrationBundleJson.cs`, bump `LocatorBestJson.SchemaVersion` literal from 2 to 3.
2. Add `double? BlurAppliedSigma = null` to `LocatorBestJson` record (positional argument or init-only property — match the surrounding style).
3. Register via the existing `[JsonSerializable(typeof(LocatorBestJson))]` source-gen context (no change needed if the type's already registered).
4. In `AutoCalibrationEngine.cs`, at the locator-best emission site (already populates `Algorithm`, `FallbackNcc`, `PadPx`, `LevelScales` per #1061), add `BlurAppliedSigma: metrics.BlurAppliedSigma` to the `LocatorBestJson` construction. One additional assignment.

**Tests:**

- **New** `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/LocatorBestJsonV3Tests.cs`:
  ```csharp
  [Fact]
  public void V2_input_round_trips_with_BlurAppliedSigma_as_null()
  {
      const string v2Json = """
      {
        "schemaVersion": 2,
        "algorithm": "sobel-padded-pyramid",
        "fallbackNcc": 0.6,
        "padPx": 100
        // (no blurAppliedSigma)
      }
      """;
      var deserialized = JsonSerializer.Deserialize<LocatorBestJson>(v2Json, CalibrationBundleJsonContext.Default.LocatorBestJson)!;
      deserialized.SchemaVersion.Should().Be(2);
      deserialized.BlurAppliedSigma.Should().BeNull();
      // Re-serializing emits v3 (the SchemaVersion field comes from the field's
      // declared default — which is now 3 — when the deserialized instance is
      // round-tripped through `with { }` or a fresh ctor). The exact contract
      // depends on the existing v1→v2 behaviour from #1061; mirror that here.
  }

  [Fact]
  public void V3_input_round_trips_with_BlurAppliedSigma()
  {
      var input = new LocatorBestJson(SchemaVersion: 3, /* …all other fields… */, BlurAppliedSigma: 0.358);
      var json = JsonSerializer.Serialize(input, CalibrationBundleJsonContext.Default.LocatorBestJson);
      var output = JsonSerializer.Deserialize<LocatorBestJson>(json, CalibrationBundleJsonContext.Default.LocatorBestJson)!;
      output.BlurAppliedSigma.Should().Be(0.358);
  }
  ```

**Acceptance:** Both v2 and v3 inputs deserialise; v3 round-trips with `BlurAppliedSigma`. Now the Hogan's OUT corpus test from Task 5 should flip green (assuming Task 0's σ defaults are reasonable) — verify by re-running. If it still fails, the σ curve may need Task 0's piecewise-fallback path.

---

## Task 7 — Telemetry, docs, INDEX, verification

**Files:**

- `docs/perf-trace-schema.md`
- [`docs/planning/INDEX.md`](../INDEX.md)
- (Post-merge) wiki Auto-Calibration Sub-Zone Findings page

**Steps:**

1. **`docs/perf-trace-schema.md`** — find the `calibration.refine.fallback` span row (added in #1061 §9). Add `blur.sigma` to its tag list with a one-line description: *"σ (px) applied to the Sobel template at the fallback's full-resolution stage. Null when blur is disabled or the FM primary won the dispatch. Mithril#1070."*
2. **`docs/planning/INDEX.md`** — append the new row (alphabetic placement after `map-calibration-1041-mapsceneref-standardization`, before `map-calibration-detection-project-split`):
   ```
   | [map-calibration-blur-aware-template-1070](map-calibration-blur-aware-template-1070/) | active | [#1070](https://github.com/moumantai-gg/mithril/issues/1070) | Blur-aware Sobel template for #1061 sparse-locate fallback — closes the NCC-peak-flatness gap that produces Mode-A wall-edge-band registration error in sub-zone interiors |
   ```
3. **Update `PerfTracerTests.cs`** (in `tests/Mithril.Shared.Tests/`) — find the byte-parity assertion for the `calibration.refine.fallback` span and extend its expected-tag list with `blur.sigma`. This is a small assertion change, not a new test file.
4. **Wiki Findings page** (after PR merges) — flip the "Open questions" row for "Is the Mode-A failure monotonic in zoom level?" from open → confirmed-monotonic-in-1/scale per the spec §1.1 corpus.
5. **`dotnet test Mithril.slnx`** — final full-build green run.

**Tests:** Just the `PerfTracerTests` update above.

**Acceptance:**

- `dotnet test` green.
- All five corpus + unit tests from Task 5 + Task 4 green.
- `INDEX.md` row visible.
- Manual smoke (one more time): re-run Hogan's at the three captured scales via the manual hotkey. Confirm `01-attempt.json.locatorBest.blurAppliedSigma` is non-null + matches `RendererBlurModel.SigmaFor(scale, options)` at the recovered scale. Confirm `07b-foreground.png` at the OUT-zoom case looks meaningfully cleaner than the spec §1.1 baseline.

---

## Out of scope (deferred to follow-up if needed)

- **Online per-attempt σ estimation** (autocorrelation-width comparison at runtime). The static-curve approach lands fastest; if the production curve under-fits a future-discovered scene class, online estimation is the next lever — separate issue.
- **σ for the coarse + half pyramid stages.** Their NCC peaks select the basin, not sub-pixel `(tx, ty)`. Blur there risks degrading basin discrimination for no precision gain.
- **Anisotropic σ.** PG is empirically isotropic (#1061 round 5); a single σ is sufficient.
- **σ for the ORB primary's keypoint detection.** ORB has its own internal blur; this spec only touches the Sobel fallback.

---

## Commit cadence

Seven tasks → seven commits. Per `collaboration_style` user memory ("frequent commits"). Each commit message uses the `feat(map-calibration): … (mithril#1070)` shape. PR title: `feat(map-calibration): blur-aware Sobel template for sparse-locate fallback (closes #1070)`.

Task 0's measurement spike is throwaway — its commit lives on the implementation branch but the `tools/MapCalibrationFromScreenshot/BlurFitSpike/` directory gets deleted in Task 7 (or the final pre-PR commit). The measured numbers it produced are the load-bearing output.
