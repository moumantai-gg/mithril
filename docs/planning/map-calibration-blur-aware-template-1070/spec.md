# Map auto-calibration: blur-aware Sobel template for the sparse-locate fallback — spec

**Issue:** [mithril#1070](https://github.com/moumantai-gg/mithril/issues/1070). **Status:** active. **Branch posture:** docs-only spec/plan PR; the implementation lands in a follow-up PR this spec drives.

**Companion:** [Auto-Calibration Sub-Zone Findings (wiki)](https://github.com/moumantai-gg/mithril/wiki/Auto-Calibration-Sub-Zone-Findings) — the 2026-06-10 corpus + verification surfaces this spec leans on.

## 1. Problem

The [mithril#1061](https://github.com/moumantai-gg/mithril/issues/1061) Sobel-padded-pyramid sparse-locate fallback (shipped by [`SobelPaddedPyramidRefiner`](../../../src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs)) recovers an `(origin, scale)` for sub-zone interiors but with a **persistent constant-pixel sub-pixel-to-low-px registration error**. The mithril#1124 detector observability surfaces this directly: `07b-foreground.png` traces every wall edge of the dungeon texture as wide bands; the morphological close step merges them into a single `Structure`-class mega-blob that covers most of the map interior; the icons inside the mega-blob's bbox get classified as Structure and dropped before they ever reach the per-blob template scorer ([mithril#1121](https://github.com/moumantai-gg/mithril/issues/1121)). [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) is the live symptom — `Map_HogansKeepBasement` accepting a cal whose `09-projection-overlay.png` shows portal projections nowhere near visible icon glyphs.

### 1.1 Evidence corpus (2026-06-10, engine `3.0.0.81+7c4d7623e6` → `3.0.0.82+1c3350381f`)

Multi-zoom Hogan's captures plus outdoor working controls give the **monotonicity confirmation** for the precision-vs-scale relationship:

| Bundle | Algorithm | recovered scale | NCC | dev above-threshold | Icon-class blobs | Structure mega-blob area |
|---|---|---|---|---|---|---|
| `Map_AreaEltibule-…101911` (outdoor accepted) | orb-lowe (266 inliers / 284, residual 0.745 px) | 0.328 | n/a | n/a | many discrete | n/a |
| `Map_AreaSerbule-…100213` (outdoor accepted) | orb-lowe (611 inliers / 621, residual 0.665 px) | 0.328 | n/a | n/a | many discrete | n/a |
| `Map_HogansKeepBasement-…154134` (zoomed OUT) | sobel-padded-pyramid | **0.280** | 0.560 | **32.0 %** | **0** | 26,545 (covers everything) |
| `Map_HogansKeepBasement-…091533` (prior) | sobel-padded-pyramid | **0.776** | 0.614 | 22.0 % | (4 solver-inliers) | 119,655 |
| `Map_HogansKeepBasement-…154213` (zoomed IN) | sobel-padded-pyramid | **0.940** | 0.761 | **11.2 %** | **5** | 102,098 |

The three sub-zone Hogan's captures span the locator's scale range monotonically. **Deviation density, NCC, and Icon-class blob survival are all monotonic in `scale`** — but the direction is the **opposite** of what the mithril#1070 issue body predicts:

- mithril#1070 body says: high zoom → more renderer blur baked in → worse template mismatch → **worse precision at high zoom**.
- Corpus says: high zoom → **better** Mode-A signature (less deviation, more Icon-blobs survive).

### 1.2 Corrected hypothesis direction

The mechanism (blur-profile mismatch between PG's renderer and the template-side Sobel-after-resize limits NCC peak sharpness, which limits sub-pixel refinement) is consistent with the data, but the dominant effect is **template-side downsample blur growing as `1/scale`**, not renderer-side blur growing with scale:

- At low recovered scale the template gets heavily downsampled (`Cv2.PyrDown` + `Cv2.Resize` chain in [`SobelPaddedPyramidRefiner.cs`](../../../src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs)). The Sobel-magnitude → INTER_AREA-resize blur profile diverges from PG's small-render blur.
- The NCC peak around the true `(tx, ty)` flattens, parabolic sub-pixel refinement loses precision, the recovered translation lands 1–2 px off the truth.
- This pixel-level error is roughly **constant in pixels**. Expressed as a fraction of the rendered texture, it scales as `1/scale`:
  - At scale 0.28 (rendered 287×287), a 1 px error is ~0.35 % of texture — every wall edge mis-aligns visibly.
  - At scale 0.94 (rendered 962×962), the same 1 px error is ~0.10 % of texture — wall edges land closer to their true positions, Icon-shaped blobs survive past the morph close.

The deeper read: the wall-edge-band pattern's *severity* scales with `1/scale` because of the constant-pixel error denominator, but the *mechanism producing the constant-pixel error* (NCC peak flatness from blur mismatch) is what we fix.

### 1.3 Why the existing knobs don't catch it

`MapCalibrationLocateOptions.FallbackNccFloor = 0.20` already exists — but Hogan's NCC sits at 0.614 (well above the floor), so the gate accepts the bad cal. Synthesis-J (mithril#1117) DOES surface the problem (J=3.25 vs `jMin=8`, `disagree="accept_to_reject"`) but runs in Shadow mode by default and is a separate lever ([mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) path-1). This spec sits upstream of both — fix the precision so the gate doesn't see a borderline case in the first place.

## 2. Goal / scope

**In scope** — close the NCC-peak-flatness gap by applying a matched Gaussian blur to the Sobel template after resize but before final-stage matchTemplate, with σ derived from a measured PG-renderer-vs-template-resize blur model.

| Change | Goal |
|---|---|
| New `RendererBlurModel` static (Detection project) | One source of truth for σ(scale) — initial fit from a one-time measurement experiment; settings-store knob lets a future tuner override. |
| `SobelPaddedPyramidRefiner` applies blur at the FINAL (full-resolution) stage, after resize, before `matchTemplate` and the response-map sub-pixel refinement | Sharpen the NCC peak at the only stage whose precision matters for `(tx, ty)` recovery. Coarse + half stages stay unchanged (they're for basin selection, not precision). |
| New knobs on `MapCalibrationLocateOptions` (per #1061 §6.5 settings-store pattern) | `RendererBlurEnabled` (default `true`), `RendererBlurSigmaSlope`, `RendererBlurSigmaIntercept`, `RendererBlurMinSigma`, `RendererBlurMaxSigma`. Schema bump v1→v2 with `Migrate` no-op (additive defaults). |
| `LocatorBestJson` schema v2→v3 — additive `BlurAppliedSigma` field | Diagnostic surface so a bundle records the σ actually applied; null when fallback didn't run or `RendererBlurEnabled=false`. |
| Multi-scale corpus test suite | Hogan's at the three measured scales (0.28 / 0.78 / 0.94) checked into `tests/Mithril.MapCalibration.Tests/Detection/blur_aware_corpus/`; assert post-fix deviation density at scale=0.28 drops measurably (target floor TBD by measurement). |

**Out of scope** (called out in §10):

- ORB-primary side improvements ([`FeatureMatchingRefiner`](../../../src/Mithril.MapCalibration.Detection/FeatureMatchingRefiner.cs)). ORB doesn't run on the sub-zone Hogan's regime; nothing changes for outdoor.
- Mode-B solver-side rejection ([mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) cross-scene portal-leak territory).
- The AutoCalibrationTrigger log-replay-suppression bug (separately tracked via task chip).
- Synthesis-J promotion from Shadow to Enabled ([mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) path-1; gated on per-area thresholds).
- Per-attempt online σ estimation (autocorrelation-width comparison between capture and template at runtime). The mithril#1070 body lists this as an alternative; this spec picks the **measured-σ-curve approach** because it lands a working fix on the existing static-fit-and-deploy pattern without adding a runtime-measurement subroutine. If the static curve later turns out to under-fit certain scenes, online estimation is a follow-up.
- Coarse + half stages of the pyramid. Those stages exist for basin selection (which scale-step neighborhood is the winner); their NCC peak position doesn't drive the recovered `(tx, ty)`. The full-resolution stage's response map does.

## 3. Decision ledger

| # | Decision | Reasoning |
|---|---|---|
| D1 | **Blur is applied to the TEMPLATE (texture-side), not to the capture.** | The mismatch this fixes is between PG's renderer-blur on its already-rendered texture in the screenshot and the template-side Sobel-after-resize blur. The capture already has PG's blur baked in; blurring it further is double-counting. The template-side blur step is the one we own and the one whose σ we can calibrate. |
| D2 | **Blur is applied at the FINAL (full-resolution) pyramid stage only.** | The coarse + half stages are for basin selection. Their NCC peak's neighborhood shape doesn't drive sub-pixel `(tx, ty)`. The full-resolution stage's response map is what sub-pixel parabolic refinement reads. Adding blur at coarse stages would risk degrading basin discrimination for zero precision benefit. |
| D3 | **σ(scale) is a static fit from a one-time measurement experiment**, persisted via `MapCalibrationLocateOptions` (settings store, per #1061's §6.5 pattern). The model is a simple two-parameter linear σ = `Intercept + Slope × (1/scale)` with floor + ceiling clamps. | Two-parameter linear fit is the smallest model that captures the "blur-grows-as-1/scale" relationship implied by §1.2. If the fit residuals are large on the measurement corpus, the spec defers to a piecewise-linear table extension (Plan Task 1 step 3). Avoids per-attempt online estimation (its own runtime cost + complexity); avoids fitting a high-order polynomial that overfits to 5–6 measurement points. |
| D4 | **The measurement experiment is `Plan Task 0`** — a one-time procedure that captures a single sub-zone scene at 4–5 known zoom levels, measures autocorrelation-width difference between capture-Sobel and template-Sobel-after-resize at each, and fits the linear σ(scale) curve. Results land in the spec's "Verification owed" + the settings-store defaults. | Fit on data, not a guess. The measurement work is enough to do once — the fitted curve becomes the production default. If the fit is wrong, a settings-store override unblocks any user without redeploy. |
| D5 | **No new `OpenCvSharp4.Contrib` dependency.** Use `Cv2.GaussianBlur` from the core package already in use. | Per #1061's "stay within current package set" constraint. `GaussianBlur` is core, no contrib needed. |
| D6 | **`LocatorBestJson` schema v2→v3 (additive)** — record `BlurAppliedSigma` (`double?`) on every fallback attempt. Null when fallback didn't run or `RendererBlurEnabled=false`. | A `07b-foreground.png` triager needs to know what σ produced the result. Bundle round-trip respects additive-field convention from #1061's v1→v2 (reader treats absence of new fields as v2). `AttemptJson.SchemaVersion` does not change — its shape is unchanged (mirror of #1061's bump pattern). |
| D7 | **`MapCalibrationLocateOptions` schema v1→v2 + Migrate**, mirroring the #1061 promotion pattern. New knobs default-on. | Additive bump; Migrate is identity for v1→v2 (the new properties initialize from constructor defaults, which is what users want — the static fit becomes the production curve). |
| D8 | **Verification surface = `07b-foreground.png` deviation pixel count (`10c-blob-pipeline.json.deviation[].aboveThresholdCount`) + Icon-class blob count, captured at multiple scales.** Acceptance bar is qualitative + quantitative: Hogan's at scale ≤ 0.30 drops from ~32 % deviation to ≤ 18 %; Icon-class blob count rises from 0 to ≥ 3. | Re-uses #1124's shipped observability as the falsifiable success criterion. The 18 % target is the midpoint between Hogan's broken (32 %) and Hogan's-IN-already-passing (11 %); if the fix doesn't move OUT meaningfully toward that midpoint, it isn't working. Final exact threshold lands in Plan Task 5 after the measurement data sets the σ curve. |
| D9 | **Test corpus is checked into `tests/`**, not pulled live from the user's diagnostics dir. The bundles ship as fixture inputs. | Reproducibility — CI doesn't have a Mithril running. The corpus is small (3 Hogan's + 1 Eltibule + 1 Serbule × small subset of files) and round-trips cleanly via existing `BuildPair`-style fixtures. |
| D10 | **No change to the FM/Composite refiner dispatch** ([`CompositeMapRegionRefiner`](../../../src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs)) or to the engine. The fix is fully local to `SobelPaddedPyramidRefiner`'s internal pipeline. | Smallest possible blast radius. ORB primary + composite logic + engine all see the same `MapRegionRefineResult` contract; the only difference is that the result's recovered `(tx, ty)` is tighter. |

## 4. Architecture overview

```
SobelPaddedPyramidRefiner.RefineCore (existing — #1061)
├─ 1. Sobel magnitude on capture + texture
├─ 2. 100-px zero pad capture
├─ 3. 3-level Gaussian pyramid (PyrDown × 2)
├─ 4. COARSE stage (quarter resolution): scale ladder, basin pick   ← unchanged
├─ 5. HALF stage (half resolution): narrow ladder around coarse     ← unchanged
└─ 6. FULL stage (full resolution): narrow ladder around half       ← INSERTION POINT A
    ├─ 6a. NarrowLadderWithLoc (existing helper, SobelPaddedPyramidRefiner.cs:226-244):
    │     For each candidate scale s in [centre−2·step, centre+2·step]:
    │     ├─ resize textureSobel to (W·s, H·s) via INTER_AREA
    │     ├─ NEW: σ = RendererBlurModel.SigmaFor(scale=s, options)
    │     ├─ NEW: if (σ > 0) Cv2.GaussianBlur(resized, resized, ksize=(0,0), σx=σ, σy=σ)
    │     ├─ matchTemplate(capPadded, resized, CCoeffNormed) → response map
    │     └─ append (scale, score, location) to fineLadder; record σ alongside (D2.a)
    ├─ 6b. ArgMax(fineLadder) → fine winner
    ├─ 6c. Parabolic peak refinement on scale axis (existing)             ← INSERTION POINT B
    │     If parabolic refinement triggers (concave-down + |sub-step| ≤ 1):
    │     ├─ refinedScale = winner + step·subStep
    │     ├─ resize textureSobel at refinedScale via INTER_AREA
    │     ├─ NEW: σ = RendererBlurModel.SigmaFor(scale=refinedScale, options)
    │     ├─ NEW: if (σ > 0) Cv2.GaussianBlur(resized, resized, ksize=(0,0), σx=σ, σy=σ)
    │     ├─ matchTemplate(capPadded, resized, CCoeffNormed) → re-matched response
    │     ├─ SobelMagnitudeHelpers.RefineLocationSubPixel(result, maxLoc) (existing)
    │     └─ bestSigma = σ (this σ supersedes the fine-ladder σ — it's the one tied to the recovered tx/ty)
    └─ 6d. EXIT — recovered (tx, ty, scale, NCC, bestSigma)
```

**Both INSERTION POINT A (the fine ladder) and INSERTION POINT B (the post-parabolic re-match) need blur** — without blur at the re-match, the parabolic refinement step would convolve an un-blurred template against the (already-blurred-by-PG) capture, producing a different NCC peak shape than the ladder's pre-parabolic shape and partially undoing the gain. The `bestSigma` carried out of the refiner is whichever σ was applied to the matchTemplate call that produced the recovered (tx, ty) — point B if it fired, otherwise point A's winning rung.

```
RendererBlurModel (new, static, in Mithril.MapCalibration.Detection.Internal)
└─ SigmaFor(scale, options) → double
   ├─ if (!options.RendererBlurEnabled) return 0.0
   ├─ raw = options.RendererBlurIntercept + options.RendererBlurSlope * (1.0 / scale)
   └─ return Clamp(raw, options.RendererBlurMinSigma, options.RendererBlurMaxSigma)
```

```
MapCalibrationLocateOptions (existing — #1061)
├─ Existing knobs unchanged
└─ NEW (schema v1→v2):
   ├─ RendererBlurEnabled : bool   (default true)
   ├─ RendererBlurIntercept : double  (default from Plan Task 0 measurement)
   ├─ RendererBlurSlope : double      (default from Plan Task 0 measurement)
   ├─ RendererBlurMinSigma : double   (default 0.0)
   └─ RendererBlurMaxSigma : double   (default 3.0 — Gaussian ksize implications)
```

```
LocatorBestJson (existing — #1061 v1→v2; current shipped fields: Algorithm, FallbackNcc, PadPx)
└─ NEW (schema v2→v3, additive):
   └─ BlurAppliedSigma : double?   (the actually-applied σ at the FULL stage's recovered scale; null if blur disabled or fallback didn't run)
```

Producer cost when `RendererBlurEnabled=false`: zero (skip the `GaussianBlur` call entirely, no σ computation either).
Producer cost when `RendererBlurEnabled=true`: one `Cv2.GaussianBlur` per fine-ladder rung evaluated at the full stage (~5 rungs × ~1 ms) **plus one for the post-parabolic re-match** when it fires (~1 ms). Total ~5–6 ms on Hogan's 1024-px texture; negligible vs the existing matchTemplate cost.

## 5. Layer-by-layer detail

### 5.1 `RendererBlurModel` (new — `Mithril.MapCalibration.Detection/Internal/RendererBlurModel.cs`)

```csharp
namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// σ(scale) curve for the mithril#1070 blur-aware template path. Linear in 1/scale
/// with a floor + ceiling clamp; coefficients persist on
/// <see cref="MapCalibrationLocateOptions"/> so the curve can be tuned without
/// recompile.
///
/// <para>The default coefficients come from the spec's Plan Task 0 measurement
/// experiment — a one-time σ-vs-scale fit on Hogan's Basement at 4–5 known
/// zoom levels.</para>
/// </summary>
internal static class RendererBlurModel
{
    /// <summary>
    /// σ to apply to a template that's been resized to <paramref name="scale"/>×
    /// native. Returns 0 when blur is disabled. Clamps to
    /// [<see cref="MapCalibrationLocateOptions.RendererBlurMinSigma"/>,
    /// <see cref="MapCalibrationLocateOptions.RendererBlurMaxSigma"/>].
    /// </summary>
    public static double SigmaFor(double scale, MapCalibrationLocateOptions options)
    {
        if (!options.RendererBlurEnabled) return 0.0;
        if (scale <= 0.0) return options.RendererBlurMinSigma;
        double raw = options.RendererBlurIntercept + options.RendererBlurSlope * (1.0 / scale);
        if (raw < options.RendererBlurMinSigma) return options.RendererBlurMinSigma;
        if (raw > options.RendererBlurMaxSigma) return options.RendererBlurMaxSigma;
        return raw;
    }
}
```

Pure static, no DI, no allocation. The σ is recomputed once per fine-ladder rung (~5 calls per attempt); a no-allocation function is the right shape.

### 5.2 `SobelPaddedPyramidRefiner.RefineCore` — blur application at the FULL stage

Today's full stage in [`SobelPaddedPyramidRefiner.cs`](../../../src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs) has TWO matchTemplate sites that both produce the final `(tx, ty)`:

1. **`NarrowLadderWithLoc`** (lines 226-244) — the fine ladder around the L1 winner, emits `List<(double Scale, double Score, Point Loc)>`. `ArgMax` picks the winning rung.
2. **The post-parabolic re-match in `RefineCore`** (lines 119-130) — when parabolic refinement triggers (concave-down NCC peak + `|sub-step| ≤ 1`), the refiner does ONE more `Cv2.Resize` + `Cv2.MatchTemplate` at the parabolic-refined scale, then calls `SobelMagnitudeHelpers.RefineLocationSubPixel(result, maxLoc)` to derive the sub-pixel `(dx, dy)`.

The recovered `(refinedTx, refinedTy)` carried out of the refiner is **whichever of those two matches fired last** — `NarrowLadderWithLoc`'s winner if parabolic didn't trigger, the re-match if it did. Blur has to apply at both, otherwise the re-match silently un-does the gain.

**INSERTION POINT A — `NarrowLadderWithLoc`** (the existing helper, lines 226-244):

```csharp
// existing signature stays; add an options ref so SigmaFor can be called
private static List<(double Scale, double Score, Point Loc, double Sigma)> NarrowLadderWithLoc(
    Mat cap, Mat tex, double centreScale, int minDim, double scaleStep,
    MapCalibrationLocateOptions options)        // NEW param — needs the σ curve
{
    var ladder = new List<(double Scale, double Score, Point Loc, double Sigma)>(8);
    for (double s = centreScale - 2 * scaleStep; s <= centreScale + 2 * scaleStep + 1e-6; s += scaleStep)
    {
        if (s <= 0) continue;
        int sw = (int)Math.Round(tex.Width * s);
        int sh = (int)Math.Round(tex.Height * s);
        if (sw < minDim || sh < minDim || sw > cap.Width || sh > cap.Height) continue;
        using var scaled = new Mat();
        Cv2.Resize(tex, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
        double sigma = RendererBlurModel.SigmaFor(s, options);                        // NEW
        if (sigma > 0.0)                                                              // NEW
            Cv2.GaussianBlur(scaled, scaled, new Size(0, 0), sigma, sigma);           // NEW
        using var result = new Mat();
        Cv2.MatchTemplate(cap, scaled, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
        ladder.Add((s, maxVal, maxLoc, sigma));                                       // CHANGED — carry σ
    }
    return ladder;
}
```

The tuple grows to four-arity to carry the σ alongside each rung. `ArgMax` is unchanged (still picks by `Score`). The caller (`RefineCore`) reads `fineWinner.Sigma` for the ladder-side σ.

**INSERTION POINT B — the post-parabolic re-match** in [`RefineCore`](../../../src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs:119) at lines 119-130:

```csharp
// existing parabolic branch — additions marked NEW:
if (sw >= minDimFull && sh >= minDimFull
    && sw <= capPadded.Width && sh <= capPadded.Height)
{
    using var scaled = new Mat();
    Cv2.Resize(texSobel, scaled, new Size(sw, sh),
        interpolation: InterpolationFlags.Area);
    double sigma = RendererBlurModel.SigmaFor(candidate, _options);                 // NEW
    if (sigma > 0.0)                                                                // NEW
        Cv2.GaussianBlur(scaled, scaled, new Size(0, 0), sigma, sigma);             // NEW
    using var result = new Mat();
    Cv2.MatchTemplate(capPadded, scaled, result, TemplateMatchModes.CCoeffNormed);
    Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
    var (sdx, sdy) = SobelMagnitudeHelpers.RefineLocationSubPixel(result, maxLoc);
    refinedScale = candidate;
    refinedTx = maxLoc.X + sdx - pad;
    refinedTy = maxLoc.Y + sdy - pad;
    refinedNcc = maxVal;
    bestSigma = sigma;                                                              // NEW
}
```

`bestSigma` defaults to the fine winner's σ before the parabolic block (so when parabolic doesn't trigger, σ from point A wins). The final value surfaces into the `LocateMetrics` returned:

```csharp
var metrics = new LocateMetrics(
    InlierCount: 0, CandidateCount: 0, InlierRatio: 0,
    Scale: refinedScale, RotationDegrees: 0, Mirror: false,
    Tx: refinedTx, Ty: refinedTy, ResidualPixels: 0,
    Provenance: LocateProvenance.SobelPaddedPyramid,
    Confidence: refinedNcc,
    BlurAppliedSigma: bestSigma);   // NEW positional arg on LocateMetrics
```

The coarse + half stages (`TryFullLadder` line 172, `TryNarrowLadder` line 199) are unchanged — they don't blur. Only the full stage's matches drive the recovered sub-pixel `(tx, ty)`. (See §3 D2 for why coarse + half are deliberately left alone — basin selection vs precision recovery, different jobs.)

### 5.3 `MapCalibrationLocateOptions` — knobs + v1→v2

Five new properties + a v1→v2 Migrate branch. Following the existing pattern in `MapCalibrationLocateOptions.cs`:

```csharp
// Defaults come from Plan Task 0 measurement; placeholders here until that lands.
private bool   _rendererBlurEnabled   = true;
private double _rendererBlurIntercept = 0.10;   // PLACEHOLDER — measured fit replaces in PR
private double _rendererBlurSlope     = 0.20;   // PLACEHOLDER — measured fit replaces in PR
private double _rendererBlurMinSigma  = 0.0;
private double _rendererBlurMaxSigma  = 3.0;
```

Migrate gains a v1→v2 branch:

```csharp
public static MapCalibrationLocateOptions Migrate(MapCalibrationLocateOptions loaded)
{
    if (loaded.SchemaVersion >= Version) return loaded;
    // v1 → v2: additive — Renderer{Blur*} properties initialize from
    // constructor defaults. No value-bearing field is renamed or
    // dropped; loading a v1 file leaves blur enabled with the spec's
    // measured defaults, which is the intended production posture.
    return loaded;
}
```

Constant `Version` bumps from 1 to 2.

### 5.4 `LocatorBestJson` — schema v2→v3, additive `BlurAppliedSigma`

Mirror of #1061's v1→v2 additive bump. New optional field:

```csharp
public sealed record LocatorBestJson(
    /* …existing v2 fields: Algorithm = "orb-lowe", FallbackNcc = null, PadPx = null… */,
    double? BlurAppliedSigma = null);
```

The current shipped v2 carries `Algorithm`, `FallbackNcc`, `PadPx` per [`CalibrationBundleJson.cs:34-54`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs#L34). (mithril#1061's spec also mentioned a `LevelScales` field; it didn't ship.) The v2→v3 bump appends one more optional positional argument; pre-#1070 readers handle the absence as null via the default.

`LocatorBestJson.SchemaVersion = 3`. Reader treats absence of `BlurAppliedSigma` as v2 (null). Sink writes v3 unconditionally; the new field is null on ORB-primary success and on Sobel-fallback runs where `RendererBlurEnabled = false`.

`AttemptJson.SchemaVersion` does NOT change — its shape is unchanged (the bundle's top-level structure stays at v3 from #1124).

### 5.5 Telemetry

The existing `calibration.refine.fallback` span (#1061 §9) is emitted by [`CompositeMapRegionRefiner.Refine`](../../../src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs#L60) using `MapCalibrationDiagnostics.ActivitySource` (defined in [`src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs`](../../../src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs), name `"Mithril.MapCalibration.Detection"`). This is a **Detection-layer** catalog, distinct from the **Capture-layer** `MithrilActivitySources.MapCalibration` in `Mithril.Shared` (name `"Mithril.MapCalibration.Capture"`); the two coexist because `Mithril.MapCalibration.csproj` deliberately doesn't reference `Mithril.Shared`.

The span gains one new tag:

- `blur.sigma` — the `BlurAppliedSigma` from `LocateMetrics`. Lowercase-dotted per convention. Set in `CompositeMapRegionRefiner.Refine`'s existing `fallbackAct?.SetTag(...)` block (the same spot that already sets `ncc` and `scale` from `m.Confidence` and `m.Scale`).

No new span, no new metric. The bundle's `BlurAppliedSigma` field is the load-bearing diagnostic surface; the telemetry tag makes it queryable in Seq alongside `ncc`, `scale`, and `outcome`.

Update sites: `docs/perf-trace-schema.md` has TWO places where this span's vocabulary appears — the high-level row at the catalog-table near line 68, and the detailed tag-list section starting around line 325. Both need the `blur.sigma` addition. Also update the byte-parity test in `tests/Mithril.Shared.Tests/PerfTracerTests.cs` when the implementation lands.

### 5.6 Logging

Per CLAUDE.md "Instrumentation is the contract." `SobelPaddedPyramidRefiner` already takes `ILogger<SobelPaddedPyramidRefiner>?`. Three new `LogTrace` lines at the FULL stage's branch points:

```text
[Trace] Mithril.MapCalibration.Detection: Blur model: scale=0.776 → σ=0.358 (enabled=True, slope=0.20, intercept=0.10).
[Trace] Mithril.MapCalibration.Detection: Blur applied: σ=0.358, template dims 795×795 → matchTemplate.
[Trace] Mithril.MapCalibration.Detection: Refine winner: scale=0.776, ncc=0.721, sigma=0.358, location=(96,−2).
```

`LogInformation` at the lifecycle milestone `Sobel-padded-pyramid: σ-curve enabled / disabled` once per refiner construction. Existing #1061 warning paths (OpenCV failure, NCC-floor reject) unchanged.

## 6. Persistence — bundle round-trip

`LocatorBestJson` v3 + new `BlurAppliedSigma` field. Existing `01-attempt.json` writers + readers in [`CalibrationBundleJson.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs) extend with one optional field; the `[JsonSerializable]` context picks up the field via the existing `JsonSourceGenerationOptions` source generator.

`map-calibration-locate.json` (the on-disk settings store, per #1061 §6.5) extends with five new keys via the existing `AddMithrilVersionedSettings<MapCalibrationLocateOptions>` extension. No new file, no new schema family — the same store gains the new knobs.

## 7. Error handling

`Cv2.GaussianBlur` on a degenerate-size input (template < kernel diameter) throws `OpenCVException`. The existing fail-soft wrapper (`SobelPaddedPyramidRefiner.Refine`'s try/catch around `RefineCore`) catches it and returns `MapRegionRefineResult.None`. No new try/catch needed.

The σ clamp `RendererBlurMaxSigma = 3.0` keeps the implicit ksize (`(σ × 6 + 1)`) at ~19 px max — well within any expected template dimension at the full stage (`MinScaledDim = 20` floor, but in practice the full stage runs at scale 0.20+ → 200+ px templates, always larger than 19 px).

## 8. Testing strategy

| Test | Project | Asserts |
|---|---|---|
| `SigmaFor_returns_zero_when_disabled` | `Mithril.MapCalibration.Tests` | `RendererBlurEnabled=false` → `SigmaFor(any scale, options) == 0`. |
| `SigmaFor_is_linear_in_inverse_scale` | `Mithril.MapCalibration.Tests` | With `Intercept=0.10, Slope=0.20`, `SigmaFor(0.5, options) == 0.50`; `SigmaFor(1.0, options) == 0.30`; `SigmaFor(0.25, options) == 0.90`. |
| `SigmaFor_clamps_to_min_max` | `Mithril.MapCalibration.Tests` | With `Max=1.0, Slope=10.0`, `SigmaFor(0.1, options) == 1.0` (clamped). With `Min=0.5, Slope=-1.0`, `SigmaFor(any, options) == 0.5` (clamped). |
| `SigmaFor_returns_min_on_zero_scale` | `Mithril.MapCalibration.Tests` | Guard against `1/0` — `SigmaFor(0.0, options) == options.RendererBlurMinSigma`. |
| `SobelPaddedPyramidRefiner_records_BlurAppliedSigma` | `Mithril.MapCalibration.Tests` | Drive `Refine(capture, texture)` with `RendererBlurEnabled=true`; assert returned `LocateMetrics.BlurAppliedSigma` is `> 0` and matches `RendererBlurModel.SigmaFor(metrics.Scale, options)`. |
| `SobelPaddedPyramidRefiner_records_zero_blur_when_disabled` | `Mithril.MapCalibration.Tests` | With `RendererBlurEnabled=false`, assert `LocateMetrics.BlurAppliedSigma == 0`. Behaviour identical to pre-#1070 (regression lock). |
| `SobelPaddedPyramidRefiner_output_is_byte_equal_with_and_without_blur_on_synthetic_no_blur_fixture` | `Mithril.MapCalibration.Tests` | Construct a synthetic capture+texture pair where the template has no inherent blur (sharp Sobel response). With `RendererBlurEnabled` true vs false, the recovered `(scale, tx, ty)` should differ by ≤ 0.5 px. Guards against the blur ruining a clean case. |
| `Hogans_OUT_corpus_deviation_density_drops_with_blur` | `Mithril.MapCalibration.Tests` | **Corpus test** — load `tests/Mithril.MapCalibration.Tests/Detection/blur_aware_corpus/hogans_out_scale028.png` + the Hogan's texture (**checked into the same `blur_aware_corpus/` dir** — Hogan's texture is NOT in `BundledData/map-calibration-baseline.json`, so `BundledBaselineLoader` can't supply it; the test must ship its own copy). Run the FULL detector pipeline (refiner → detect → blob classify), assert `aboveThresholdCount / (W*H) < 0.18` (vs the pre-fix 0.32). |
| `Hogans_IN_corpus_already_passing_doesnt_regress` | `Mithril.MapCalibration.Tests` | **Corpus test** — same corpus directory, scale=0.94 case, assert `aboveThresholdCount / (W*H) ≤ 0.15` (vs the pre-fix 0.11 baseline, 4 % slack for noise). |
| `Eltibule_outdoor_doesnt_regress` | `Mithril.MapCalibration.Tests` | **Corpus test** — Eltibule capture goes through ORB primary, not the Sobel fallback. Assert `Algorithm == "orb-lowe"` on the recovered metrics and no blur is applied. Regression-only — ensures Composite refiner dispatch is unchanged. |
| `MapCalibrationLocateOptions_v1_migrates_to_v2_with_defaults` | `Mithril.MapCalibration.Tests` (or `Mithril.MapCalibration.Capture.Tests` if that's where settings tests live) | Deserialize a v1 JSON `{ "schemaVersion": 1, "fallbackNccFloor": 0.30 }`; assert `Migrate` returns an instance with `SchemaVersion = 2`, `FallbackNccFloor = 0.30` (preserved), `RendererBlurEnabled = true`, `RendererBlurIntercept = <measured default>`, `RendererBlurSlope = <measured default>`. |
| `LocatorBestJson_v2_round_trips_without_BlurAppliedSigma` | `Mithril.MapCalibration.Capture.Tests` | Deserialize a v2 JSON without `BlurAppliedSigma`; assert the field round-trips as `null`; assert `SchemaVersion` reads as 2 from input, serializes as 3 from a fresh instance. |

The corpus test fixtures (PNG inputs + expected metrics) come from Plan Task 5. The expected-floor numbers in the corpus tests are the spec's acceptance bar; they hold only after the Plan Task 0 measurement σ-fit lands.

## 9. Files touched (anticipated)

### 9.1 `src/`

| File | Change |
|---|---|
| `src/Mithril.MapCalibration.Detection/Internal/RendererBlurModel.cs` | **new** — static `SigmaFor(scale, options)` per §5.1. |
| [`src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs`](../../../src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs) | Apply `Cv2.GaussianBlur` after resize at BOTH full-stage matchTemplate sites: (a) inside `NarrowLadderWithLoc` (lines 226-244) for each fine-ladder rung; (b) inside the post-parabolic re-match in `RefineCore` (lines 119-130). Track + return `BlurAppliedSigma` on the returned `LocateMetrics`. Three new `LogTrace` lines. |
| [`src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptions.cs`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptions.cs) | Five new properties + getters/setters + `OnChanged` per §5.3. Bump `Version = 2`. `Migrate` v1→v2 is identity (additive defaults). |
| [`src/Mithril.MapCalibration/LocateMetrics.cs`](../../../src/Mithril.MapCalibration/LocateMetrics.cs) | Add `double? BlurAppliedSigma = null` as a new positional record argument after `Confidence`. **Note**: `LocateMetrics` lives in the base `Mithril.MapCalibration` project, not in `.Detection`. |
| [`src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs`](../../../src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs) | Add `fallbackAct?.SetTag("blur.sigma", m.BlurAppliedSigma)` next to the existing `ncc`/`scale` tag-set calls (lines 66-67), guarded on `m.BlurAppliedSigma is double` to avoid double-tagging null. |
| [`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs) | `LocatorBestJson.SchemaVersion = 3`; add `BlurAppliedSigma : double?` per §5.4. Register via existing `[JsonSerializable]` context. |
| [`src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs) | Thread `BlurAppliedSigma` from the refiner's `LocateMetrics` into the `LocatorBestJson` payload at write-time. One additional assignment. |

### 9.2 `tests/`

| File | Change |
|---|---|
| `tests/Mithril.MapCalibration.Tests/Detection/RendererBlurModelTests.cs` | **new** — 4 unit tests per §8 (`SigmaFor_*`). |
| `tests/Mithril.MapCalibration.Tests/Detection/SobelPaddedPyramidRefinerBlurTests.cs` | **new** — 3 refiner-integration tests (`records_BlurAppliedSigma`, `records_zero_blur_when_disabled`, `output_is_byte_equal_with_and_without_blur_on_synthetic_no_blur_fixture`). |
| `tests/Mithril.MapCalibration.Tests/Detection/HogansBlurAwareCorpusTests.cs` | **new** — 3 corpus tests (`Hogans_OUT_corpus_deviation_density_drops_with_blur`, `Hogans_IN_corpus_already_passing_doesnt_regress`, `Eltibule_outdoor_doesnt_regress`). |
| `tests/Mithril.MapCalibration.Tests/Detection/blur_aware_corpus/` | **new** — checked-in fixture PNGs (Hogan's OUT, Hogan's IN, Eltibule outdoor) extracted from the 2026-06-10 bundles. |
| `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/LocatorBestJsonV3Tests.cs` (or equivalent) | **new** — round-trip test for v2-without + v3-with `BlurAppliedSigma`. |
| `tests/Mithril.MapCalibration.Tests/MapCalibrationLocateOptionsV2MigrateTests.cs` (or equivalent) | **new** — v1→v2 Migrate identity test. |

### 9.3 `docs/`

- This `spec.md` + sibling `plan.md` (slug: `map-calibration-blur-aware-template-1070`)
- Append a row to [`docs/planning/INDEX.md`](../INDEX.md)
- Update `docs/perf-trace-schema.md` — add the `blur.sigma` tag to the `calibration.refine.fallback` row (§5.5)

### 9.4 Wiki (after PR merges)

- Flip the "Open questions" row in [Auto-Calibration Sub-Zone Findings](https://github.com/moumantai-gg/mithril/wiki/Auto-Calibration-Sub-Zone-Findings) for "Is the Mode-A failure monotonic in zoom level?" from open → confirmed. The 2026-06-10 corpus in §1.1 of this spec is the evidence.

## 10. Out of scope

- **ORB primary improvements** ([`FeatureMatchingRefiner`](../../../src/Mithril.MapCalibration.Detection/FeatureMatchingRefiner.cs)). ORB doesn't run on sub-zone interiors; outdoor maps see no change.
- **Mode-B solver-side rejection** ([mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) cross-scene portal-leak territory). The locator delivers a tighter basin to the solver, but the solver's own decisions (gate floor, inlier set selection, cross-scene leak) are unchanged.
- **`AutoCalibrationTrigger` log-replay-suppression** — separate bug, tracked via the task chip spawned during this brainstorm; bundle contamination signature is documented in the wiki Findings page.
- **Synthesis-J promotion from Shadow to Enabled** ([mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) path-1). The blur-aware fix should make synthesis-J score this regime above its `jMin` floor, which lets a future promotion gate-on-J without per-area threshold concerns — but the promotion itself is a separate spec.
- **Online per-attempt σ estimation** (autocorrelation-width comparison of capture vs template at runtime). Listed as an alternative in mithril#1070's body; not picked here because the static-curve approach lands fastest. If the static curve under-fits a category of scenes, online estimation is a follow-up issue.
- **Renderer blur model for the ORB primary's keypoint detection path.** ORB's keypoint extractor has its own internal blur; #1070 only touches the Sobel-padded-pyramid fallback.
- **Coarse + half pyramid stages.** Their NCC peaks select the basin, not the sub-pixel `(tx, ty)`. No blur at those stages — would risk hurting basin discrimination.
- **Anisotropic σ** (different σx vs σy). PG is empirically isotropic per #1061 round 5; isotropic σ is sufficient.

## 11. Verification owed

| Claim | How to verify |
|---|---|
| The σ(scale) curve actually monotonic + linear on the measurement corpus. | Plan Task 0 step 3: fit the data, check residuals < 10 %. If residuals exceed that, defer to piecewise-linear table (Plan Task 1 step 3) and update this spec's D3. |
| The fix doesn't introduce a regression on the previously-passing Hogan's IN case (scale 0.94, deviation 11.2 %). | Corpus test `Hogans_IN_corpus_already_passing_doesnt_regress` (§8). |
| The fix doesn't change ORB primary's behaviour for outdoor scenes. | Corpus test `Eltibule_outdoor_doesnt_regress` (§8). |
| Mode A's `07b-foreground.png` wall-edge-band pattern actually collapses post-fix toward Serbule-shape per-icon blobs. | Manual smoke after implementation: re-run Hogan's at scale 0.28 + 0.776 + 0.94, eyeball `07b-foreground.png` against the corpus screenshots in this spec's §1.1. Quantify via the corpus test's `aboveThresholdCount` floor. |
| The fix's downstream effect on Mode B (solver-side rejection). The expectation is that the locator delivers more icon-class blobs to the solver, which then finds ≥4 inliers more reliably — but it's an indirect knock-on, not the locator-precision fix's job. | Post-implementation: re-run the four 2026-06-10 sub-zone Hogan's bundles (091533 + 154134 + 154213 + the still-active TopFloor 095806) and check whether `outcome` flips from rejected-solve to accepted with reasonable J. If yes, document as a positive externality on the wiki Findings page; if no, the Mode-B story stays unchanged and #1116 remains the right home for solver-side work. |
| The `BlurAppliedSigma` field appears in every post-fix Sobel-fallback bundle. | Manual smoke: trigger a fresh Hogan's auto-cal, `cat 01-attempt.json | jq .locatorBest.blurAppliedSigma`; expect a non-null double > 0. |

## 12. Cross-references

- [mithril#1070](https://github.com/moumantai-gg/mithril/issues/1070) — this spec's home issue. Body's hypothesis direction is corrected by this spec's §1.2.
- [mithril#1061](https://github.com/moumantai-gg/mithril/issues/1061) (shipped, [PR #1071](https://github.com/moumantai-gg/mithril/pull/1071)) — the sparse-locate fallback this spec extends. §6.5 settings-store pattern is the model for this spec's D7.
- [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) — Hogan's Basement cal-quality umbrella; Mode-B territory + synthesis-J promotion path-1.
- [mithril#1117](https://github.com/moumantai-gg/mithril/pull/1118) (shipped) — synthesis-J shadow-mode observability; the J=3.25 reading in §1.3 depends on this surface.
- [mithril#1123](https://github.com/moumantai-gg/mithril/pull/1124) (shipped) — detector-pipeline observability; `07b-foreground.png` is the load-bearing verification surface (D8).
- [mithril#1124](https://github.com/moumantai-gg/mithril/pull/1124) — the PR shipping #1123's observability. Without it this spec's evidence corpus wouldn't exist.
- [Auto-Calibration Sub-Zone Findings (wiki)](https://github.com/moumantai-gg/mithril/wiki/Auto-Calibration-Sub-Zone-Findings) — companion notebook page; this spec's §1.1 corpus is referenced there as the Mode-A monotonicity validation.
- [Legolas Calibration Findings (wiki)](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings) — the world→texture-pixel transform under a known-correct locator. The locator we're sharpening here.
- [`docs/planning/calibration-pipeline-observability-1123/spec.md`](../calibration-pipeline-observability-1123/spec.md) — structural template for this spec.
- [`docs/planning/map-calibration-sparse-locate-fallback-1061/spec.md`](../map-calibration-sparse-locate-fallback-1061/spec.md) — the locate fallback this spec extends.
- [`docs/perf-trace-schema.md`](../../perf-trace-schema.md) — telemetry shape contract; the `blur.sigma` tag addition lands here.
