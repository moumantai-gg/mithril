# Map auto-calibration: deviation-map mask (boundary + fog-of-war) — spec

**Issue:** [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116). **Status:** brainstorm 2026-06-12, spec landing now. **Prereq:** [`sidecar-rgba-alpha-surface`](../sidecar-rgba-alpha-surface/spec.md) — must ship first.

**Companion:** [Auto-Calibration Sub-Zone Findings (wiki)](https://github.com/moumantai-gg/mithril/wiki/Auto-Calibration-Sub-Zone-Findings) — the 2026-06-10 Hogan's + TopFloor corpus this spec leans on; the page will be updated with this spec's mechanism re-frame once the spec lands.

## 1. Problem

[mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) catalogues a class of indoor-sub-zone auto-cal failures whose visible symptom is `09-projection-overlay.png` rendering reference landmark crosses 200+ px from where the NPC pips actually are in the screenshot. The umbrella ticket previously attributed this to locator-side precision error — the [mithril#1070](https://github.com/moumantai-gg/mithril/issues/1070) blur-aware template fix was tried (shipped [#1132](https://github.com/moumantai-gg/mithril/pull/1132)) and didn't move the headline symptom.

A 2026-06-12 brainstorm re-examined the evidence:

1. **Locator residual is bounded.** Four manual eyeball overlays of `05-base-texture-resampled.png` against `06-aligned-screenshot.png` on Hogan's 091533 across all four template quadrants (lower-center, upper-right, lower-left, upper-left) measure `dx = 0 ± 1 px` everywhere and `dy` linear in y from `~-2 px` at the top to `~0 px` at the bottom. The locator is recovering `(tx, ty, scale)` within ≤3 px Y / ≤1 px X. Not load-bearing for a 200+ px symptom.
2. **The `07b-foreground.png` wall-edge-band signature is content-mismatch + masking-gap, not registration error.** The user's overlay of 07b on 06 in GIMP showed the white-lit regions break down as: (a) **floor-wall boundary edges + a ~3-5 px buffer**, (b) **fog-of-war holes** where the player hasn't revealed, (c) **NPC-pip icons** (the real signal). The floor *interior* matches well between texture and screenshot — both shoulder a stippled-or-photographic floor pattern that scores high NCC. The edge band fires because PG's runtime softens the floor edge into fog while the texture asset is sharp; the fog-of-war holes fire because the screenshot has no detail there while the texture does.
3. **Cross-corpus confirmation.** The same signature appears in Goblin Dungeon TopFloor 095806 (fully explored — no fog-of-war component, only the boundary-band component). The mechanism reproduces across scenes. Goblin Dungeon main 095904 shows a clean 07b — it's a different (Mode-B, [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) cross-scene-leak territory) failure mode not addressed by this spec.

### 1.1 Failure chain

1. **Locator** recovers `(tx, ty, scale)` within ≤3 px. **OK.**
2. **Deviation thresholding** (per-pixel `dev[i] >= devThr` at [`DeviationBlobDetector.cs:79-83 / 117-120`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs#L79-L120)) fires across (a) the dilated floor-wall boundary band — locator residual + PG's soft-edge rendering vs the texture's sharp alpha edge produces a band of asymmetric local variance that [`addedOnly:true`](../../../src/Mithril.MapCalibration.Detection/LocalNccDeviation.cs#L33-L42) can't fully suppress — (b) **fog-region EDGES** between revealed and unrevealed floor (fog *interior* is properly suppressed by `addedOnly`; the boundary still fires because the screenshot has variance there and the texture has detail there), (c) NPC pips. The floor *interior* and the not-floor *exterior* match well.
3. **Rim subtract** (existing — [`DeviationBlobDetector.cs:128-164`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs#L128-L164), `RimMaskMode.DeviationFlood` default) excises the edge-connected rim component. Interior wall-edge bands and fog-edges remain in the working `fg` buffer.
4. **Morph-close** (kernel `closeRadius: 1`, [`DeviationBlobDetector.cs:174`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs#L174)) stitches the boundary bands and fog edges + nearby NPC-pip pixels into a single connected component web.
5. **Connected-components label + classify** ([`DeviationBlobDetector.cs:193 / Classify lines 261-276`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs#L261-L276)) sees the web as ONE large blob with high `meanDev` (0.95+) → `BlobClass.Structure` (the `aspect ≥ 2.2 || meanDev ≥ 0.6` branch). NPC-pip pixels that morph-close-MERGED into the web are now part of that single Structure component — they lose their identity as Icon-shaped components. The bundle records this as a single Structure with `area = 119,655 / 26,545` on Hogan's 091533 / 154134.
6. **Solver** has only periphery Icons that DIDN'T morph-merge with the web (~60 surviving in Hogan's 091533 at scale 0.776; ~5 in 154213 at scale 0.94). With cross-scene-leaked references in the pool, it finds a 4-inlier "geometrically inconsistent fit" whose 4 correspondences include mis-matches.
7. **Projection overlay** renders the wonky cal → green crosses on fog 200+ px from real NPC pips. **#1116 symptom.**

The leverage point is step 2 — stop the boundary band from firing in the first place. Once boundary pixels are zeroed pre-morph, the morph-close has no wall network to merge Icon pixels into; Icons stay as separate connected components; the classifier sees them and labels them Icon; the solver gets enough correspondences; the cal lands correctly.

### 1.2 Evidence corpus (2026-06-10)

Pre-existing bundles under `%LocalAppData%/Mithril/diagnostics/calibration/`:

| Bundle | Algorithm | NCC | scale | dev above-threshold | Icon-class blobs | Structure mega-blob area | 07b shape |
|---|---|---|---|---|---|---|---|
| `Map_HogansKeepBasement-…091533` (accepted) | sobel-padded-pyramid | 0.614 | 0.776 | 22.0 % | 4 solver-inliers | 119,655 | dense boundary bands + fog-of-war holes + interior icons swallowed |
| `Map_HogansKeepBasement-…154134` (rejected-solve) | sobel-padded-pyramid | 0.560 | 0.280 | 32.0 % | 0 | 26,545 (covers everything) | mega-blob fills frame; locator recovered too-small scale, every icon swallowed |
| `Map_HogansKeepBasement-…154213` (rejected-solve) | sobel-padded-pyramid | 0.761 | 0.940 | 11.2 % | 5 | 102,098 | boundary bands clearly visible, fewer fog holes (more revealed) |
| `Map_GoblinDungeon_TopFloor-…095806` (rejected, insufficient inliers) | sobel-padded-pyramid | 0.669 | 0.660 | n/a | 3 | n/a | **no fog-of-war**, only boundary bands; mechanism reproduces cleanly without fog confound |
| `Map_GoblinDungeon-…095904` (rejected-solve) | sobel-padded-pyramid | 0.626 | 0.580 | n/a | n/a | n/a | **clean per-icon blobs, no mega-blob** — Mode-B failure, NOT this spec's territory |

### 1.3 Why the existing knobs don't catch it

- `FallbackNccFloor = 0.20` accepts all of the bundles above. The locator is recovering a basin tight enough to score above it. The gate is the wrong instrument for this failure shape.
- Synthesis-J ([mithril#1117](https://github.com/moumantai-gg/mithril/pull/1118)) DOES surface the problem (`J = 3.25 vs jMin = 8`) but is Shadow-mode default. Promoting it is the [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) path-1 work; tighter detection makes the score above floor without needing the Shadow→Enabled flip.
- [mithril#1070](https://github.com/moumantai-gg/mithril/issues/1070) blur-aware template was tried in [#1132](https://github.com/moumantai-gg/mithril/pull/1132); the σ-curve fit clamped to 0 at moderate zoom (no-op) and the post-impl note (§0 of that spec) flags it didn't address the wall-edge-band regime. This spec sits at a different pipeline layer (detector deviation, not locator NCC) and explains why.

## 2. Goal / scope

**In scope** — extend the existing rim-mask subsystem at [`DeviationBlobDetector.DetectIconBlobs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs) to subtract a **second mask** from the working `fg` buffer alongside the existing `DeviationFloodRimMask`. The new mask combines:
- a **texture-alpha-derived floor-boundary band** (primary fix; closes the bulk of the symptom), and
- a **screenshot-side fog-of-war detector** (residual coverage for fog-region EDGES that `addedOnly:true` doesn't fully suppress).

Pixels in the combined mask have `fg[i] = false` before morph-close. No new "mask step between locator and detector"; the change is local to `DetectIconBlobs`.

| Change | Goal |
|---|---|
| New `FloorBoundaryMaskCache` (Detection-layer) | Edge-detect the texture's alpha channel; dilate by `BoundaryDilationPx`; cache by area key. Surface load via the prereq `IBaseTextureProvider.TryGetTextureAlpha`. **Primary fix.** |
| New `FogOfWarDetector` (Detection-layer) | Per-attempt: local-variance + luminance-window on the recovered region. Returns the fog-edge mask. **Residual coverage**: `addedOnly:true` already suppresses fog-INTERIOR; this catches fog-region EDGES where the screenshot's variance from the fog falloff creates asymmetric `va > vb`. |
| New `DeviationMaskCombiner` (Detection-layer) | OR-combine the two masks. |
| Modified [`DeviationBlobDetector.DetectIconBlobs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs) | Accept the combined mask as a parameter; subtract it from `fg` after thresholding + existing rim subtract, before morph-close. Order: threshold → rim subtract → **NEW mask subtract** → morph-close. Diagnostic hook for the new subtract mirrors the existing `OnRimMask` pattern. |
| New `MapCalibrationDetectorOptions` class (doesn't exist today; confirmed via grep) | Carries `DeviationMaskingEnabled`, `BoundaryDilationPx`, `FogOfWarDetectionEnabled`, `FogVarianceThreshold`, `FogColorMin`, `FogColorMax`. Schema v1, identity Migrate. Registered via `AddMithrilVersionedSettings` parallel to `MapCalibrationLocateOptions`. |
| `DetectionRequest` (existing) gains optional `GrayImage? DeviationMask` field | How the AutoCal engine threads the mask through to the detector. |
| `AttemptJson.SchemaVersion` v3 → v4 (additive) | New `DeviationMask` field on `AttemptFilesJson` carrying the `07a-deviation-mask.png` path. `LocatorBestJson` and `SynthesisJson` are unaffected. |
| New `07a-deviation-mask.png` bundle artifact | Saved PNG of the combined mask. Parallel to the existing `07c-rim-mask.png` artifact written by the detector's `OnRimMask` hook. |
| Telemetry on the existing `calibration.refine.fallback` span | Tags: `mask.boundary.available`, `mask.boundary.degenerate`, `mask.fog.available`, `mask.coverage`. |
| Corpus test suite | Hogan's 091533 + TopFloor 095806 + Eltibule outdoor regression lock, checked into `tests/Mithril.MapCalibration.Tests/Detection/boundary_mask_corpus/`. |

**Out of scope** (called out in §10):

- Mode-B solver-side rejection (cross-scene leak / sparse reference pool — Goblin Dungeon main 095904 territory). Belongs to a separate sub-issue under #1116.
- Locator's ≤3 px Y-anisotropic-scale residual (~0.4 % slope). File as separate locator follow-up sub-issue under #1116; not load-bearing for the headline symptom.
- Synthesis-J Shadow→Enabled promotion ([mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) path-1). The mask should make J score above `jMin` for the Mode-A scenes, but the promotion gate is a separate decision.
- The locator and `SobelPaddedPyramidRefiner` itself — no changes.
- The solver — no changes. (If the solver still produces wonky 4-inlier fits even with cleaned detections, that's the cross-scene-leak follow-up, not this spec.)

## 3. Decision ledger

| # | Decision | Reasoning |
|---|---|---|
| D1 | **Mask subtract sits INSIDE `DeviationBlobDetector.DetectIconBlobs`, right after the existing rim subtract, before morph-close.** | The existing pipeline already does mask subtraction at this point ([`DeviationBlobDetector.cs:128-164`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs#L128-L164) — `RimMaskMode.DeviationFlood`). The new mask is an additional subtract using the same `fg[i] = false` mutation. Mask BEFORE morph-close = pixels can't connect through the masked region; mask AFTER morph-close = blobs have already formed (band-aid). Step ordering: threshold → rim subtract → **NEW mask subtract** → morph-close → connected components → classify. |
| D2 | **Floor boundary mask is texture-side, fog-of-war mask is screenshot-side, combined by OR.** Boundary mask is the primary fix; fog mask is residual coverage. | They derive from different inputs. The floor boundary is intrinsic to the area's texture (cacheable per area). The fog-of-war is intrinsic to the screenshot (per-attempt). [`LocalNccDeviation.DeviationMap`'s `addedOnly:true` mode](../../../src/Mithril.MapCalibration.Detection/LocalNccDeviation.cs#L33-L42) already suppresses fog INTERIOR pixels (the doc comment calls it "the correct fog discriminator"); the fog detector is for residual fog-EDGE coverage where the screenshot's variance from the fog falloff produces `va > vb` and slips through `addedOnly`. If the corpus tests show boundary mask alone reaches the acceptance bar, the fog detector can be disabled by default and revisited as a follow-up. |
| D3 | **Floor boundary mask uses texture ALPHA, not luminance threshold.** | Alpha is the contract: opaque = floor, transparent = not-floor. Brainstorm confirmed the user's intuition that PG's source textures are RGBA with alpha defining floor extent. Luminance-thresholding (option 2 in the alpha-availability dialogue) is a heuristic that breaks on darker indoor textures; alpha is universal. |
| D4 | **Sidecar contract extension is a HARD prereq.** | `IBaseTextureProvider.TryGetBaseTexture` returns `GrayImage?` (single-channel, no alpha — `CachedBaseTextureProvider.cs:16-17`). The sidecar discards alpha at decode. Surfacing alpha requires extending the sidecar's decode pipeline + adding a parallel `map-texture-<area>-alpha.{json,bin}` cache file + adding `TryGetTextureAlpha` to `IBaseTextureProvider`. Spec at [`../sidecar-rgba-alpha-surface/spec.md`](../sidecar-rgba-alpha-surface/spec.md). |
| D5 | **`BoundaryDilationPx = 8` default** (locator ≤3 px residual + AA edge ~3 px + fog falloff ~3 px). Tunable via settings store. | Empirically derived from the Hogan's overlay measurements; conservative enough to absorb the residual but small enough to leave most of the floor interior visible to the deviation step. Final value tunable post-corpus-measurement. |
| D6 | **Fog-of-war detector: local-variance + luminance window.** Fog pixels are characterized by low local edge density (Sobel magnitude / 7×7 variance below threshold) AND luminance in a fog-color window (default `[110, 140]` grey, based on PG screenshots). Both conditions required to flag. | A floor patch with low icon density would be low-variance but bright (above the fog luminance window). A bright fogless region would be high-variance and bright. Only fog satisfies both. Tuning constants land via measurement on the Hogan's + TopFloor corpus. |
| D7 | **Fail-soft (mirror of existing pipeline).** Mask unavailable → log warning + telemetry tag + fall through to single-mask or unmasked operation. Never error a calibration attempt because masking failed. | Matches the existing `IBaseTextureProvider` "miss → null → safe-degrade" pattern (`IBaseTextureProvider.cs:20-22`). The solver gate is the safety net, not the mask. |
| D8 | **Corpus acceptance bar:** Hogan's 091533 — Structure mega-blob area drops from 119,655 to ≤ 30,000 px AND Icon-class blob count rises from ~50 to ≥ 60. TopFloor 095806 — Structure mega-blob area drops, no fog false-positives. Eltibule outdoor — no behavioral change vs pre-fix within tolerance. | Quantitative falsifiable criteria derived from the existing `10c-blob-pipeline.json` observability. Final exact thresholds land in plan after the BoundaryDilationPx + fog-detector tuning measurements. |
| D9 | **Schema bumps are all additive identity-Migrate.** | Mirrors the existing pattern across calibration specs (#1061, #1070, #1124). No behavior change on load; settings-defaults give the production posture. |
| D10 | **`MapCalibrationDetectorOptions` is the home for the new knobs**, NOT `MapCalibrationLocateOptions`. | The masking is a *detector* concern, the locate options are for the *locator* path. Keeping them separated avoids the locator's settings file growing with non-locator content. **Confirmed via grep**: `MapCalibrationDetectorOptions` does not exist today; this spec creates it as a new class at `src/Mithril.MapCalibration.Detection/MapCalibrationDetectorOptions.cs` parallel to the existing `MapCalibrationLocateOptions.cs`. Persists to `map-calibration-detector.json` next to `map-calibration-locate.json`. |

## 4. Architecture overview

The change is localized to `DeviationBlobDetector.DetectIconBlobs`. The AutoCal engine builds the combined mask before invoking the detector and threads it through `DetectionRequest.DeviationMask`.

```
AutoCal engine (existing — Mithril.MapCalibration.Capture)
        │
        ├─ Locator runs (ORB primary / Sobel fallback) → (tx, ty, scale)
        │
        ├─ NEW: Build deviation mask
        │       │
        │       ├─ FloorBoundaryMaskCache.GetOrCompute(areaKey)
        │       │     cache hit  → return cached GrayImage
        │       │     cache miss → IBaseTextureProvider.TryGetTextureAlpha
        │       │                  → edge-detect alpha → dilate → cache
        │       │
        │       ├─ FogOfWarDetector.Detect(screenshot, roi)   [optional]
        │       │     local-variance + luminance window → fog-edge mask
        │       │
        │       └─ DeviationMaskCombiner.Combine(boundary, fog) → mask
        │
        ▼
   DetectionRequest { Screenshot, BaseTexture, …, NEW DeviationMask }
        │
        ▼
 DeviationBlobCalibrationDetector.Detect (existing)
        │
        ▼
 DeviationBlobDetector.DetectIconBlobs (existing, gains mask param)
   ├─ dev = LocalNccDeviation.DeviationMap(addedOnly: true)   (unchanged)
   ├─ Threshold:  fg[i] = (dev[i] >= devThr)                   (unchanged)
   ├─ Rim subtract:  fg[i] &= !rimMask[i]                      (unchanged)
   ├─ NEW: Deviation-mask subtract:  fg[i] &= !deviationMask[i]
   ├─ Morph-close                                              (unchanged)
   ├─ Connected components → BlobFeat list                     (unchanged)
   └─ Classify                                                 (unchanged)
        │
        ▼
 AutoCal engine writes 07a-deviation-mask.png alongside
   07c-rim-mask.png (existing); bumps AttemptJson v3 → v4
   with additive AttemptFilesJson.DeviationMask field.
```

**Cost when `DeviationMaskingEnabled=false`:** zero (skip both mask computations; deviation step runs as today).

**Cost when `DeviationMaskingEnabled=true`:**
- FloorBoundaryMaskCache: ~5-10 ms first call per area, ~0 ms subsequent (in-memory cache hit).
- FogOfWarDetector: ~5-15 ms per attempt (one local-variance + one threshold on ~800×800 roi).
- DeviationMaskCombiner: <1 ms (bitwise OR).
- Modified deviation step: ~0 ms additional (Mat multiplication or per-pixel gate).
- Bundle artifact save: ~2-5 ms PNG encode.

**Total: ~10-20 ms additional per attempt; effectively free on warm cache. Locator timing unchanged.**

## 5. Layer-by-layer detail

### 5.1 `IBaseTextureProvider.TryGetTextureAlpha` (prereq — see [sidecar spec](../sidecar-rgba-alpha-surface/spec.md))

```csharp
public interface IBaseTextureProvider
{
    GrayImage? TryGetBaseTexture(string mapAssetKey);       // existing
    GrayImage? TryGetTextureAlpha(string mapAssetKey);      // NEW
}
```

`TryGetTextureAlpha` returns the texture's alpha channel as `GrayImage` (single-channel, 0 = transparent / 255 = opaque). Same width × height as `TryGetBaseTexture` for the same `mapAssetKey`. Same null-on-miss semantics. Backed by a parallel `map-texture-<area>-alpha.{json,bin}` cache file the sidecar writes alongside the existing gray-pixel cache.

### 5.2 `FloorBoundaryMaskCache` (new — `Mithril.MapCalibration.Detection/Internal/FloorBoundaryMaskCache.cs`)

```csharp
internal sealed class FloorBoundaryMaskCache
{
    private readonly IBaseTextureProvider _textureProvider;
    private readonly MapCalibrationDetectorOptions _options;
    private readonly Dictionary<string, GrayImage> _cache = new();
    private readonly object _gate = new();
    private readonly ILogger? _logger;

    public GrayImage? GetOrCompute(string mapAssetKey)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(mapAssetKey, out var cached)) return cached;
        }
        var alpha = _textureProvider.TryGetTextureAlpha(mapAssetKey);
        if (alpha is null)
        {
            _logger?.LogWarning(
                "Floor boundary mask unavailable for {MapAsset} (sidecar alpha miss); fog mask only.",
                mapAssetKey);
            return null;
        }
        var mask = ComputeBoundaryMask(alpha, _options.BoundaryDilationPx);
        lock (_gate)
        {
            _cache[mapAssetKey] = mask;
        }
        return mask;
    }

    private static GrayImage ComputeBoundaryMask(GrayImage alpha, int dilationPx) { ... }
    // Implementation: Sobel on alpha → threshold to binary edge → dilate by dilationPx
}
```

`ComputeBoundaryMask` is a pure function suitable for unit testing on synthetic alpha mats. Implementation uses `Cv2.Sobel` for edge detection, `Cv2.Threshold` for binarization, `Cv2.Dilate` with a structuring element of size `(2*dilationPx+1)` for the buffer.

### 5.3 `FogOfWarDetector` (new — `Mithril.MapCalibration.Detection/Internal/FogOfWarDetector.cs`)

```csharp
internal sealed class FogOfWarDetector
{
    private readonly MapCalibrationDetectorOptions _options;

    public GrayImage Detect(GrayImage screenshotRoi)
    {
        // 1. Compute local variance (7×7 window) via the standard
        //    E[X²] - E[X]² formula using Cv2.BoxFilter twice.
        // 2. Compare variance against FogVarianceThreshold → low-variance binary mask.
        // 3. Compare per-pixel luminance against [FogColorMin, FogColorMax] → luminance mask.
        // 4. Bitwise AND the two → fog mask.
    }
}
```

### 5.4 `DeviationMaskCombiner` (new — small static class)

```csharp
internal static class DeviationMaskCombiner
{
    public static GrayImage Combine(GrayImage? floor, GrayImage? fog, int width, int height)
    {
        // OR-combine. Null mask = "nothing masked" for that source.
    }
}
```

### 5.5 Modified [`DeviationBlobDetector.DetectIconBlobs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs)

Three call sites change in this function. Mask subtract goes right after the existing rim subtract block (around current line 164) and before morph-close (current line 174):

```csharp
public static IReadOnlyList<BlobFeat> DetectIconBlobs(
    float[] dev, int w, int h, double lowNcc, RimMaskMode rim, BlobOptions opts, int closeRadius,
    DetectionDiagnosticHooks? hooks = null,
    double meanNcc = double.NaN,
    ILogger? logger = null,
    bool[]? deviationMask = null)        // NEW — null = preserve existing behavior
{
    /* …existing threshold loop (lines 79-126) unchanged… */

    /* …existing rim subtract (lines 128-164) unchanged… */

    // NEW: deviation-mask subtract. Mirrors the rim subtract's shape; emits
    // OnDeviationMask hook record when wired, null-fast-path otherwise.
    if (deviationMask is not null)
    {
        if (hooks?.OnDeviationMask is not null)
        {
            int maskedCount = 0, survivorCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (deviationMask[i]) { maskedCount++; fg[i] = false; }
                if (fg[i]) survivorCount++;
            }
            hooks.OnDeviationMask(new DeviationMaskSnapshot(
                Rotate180: false, Width: w, Height: h,
                MaskPixelCount: maskedCount,
                FgInputCount: /* pre-subtract count */,
                FgSurvivorCount: survivorCount,
                MaskBuffer: ((bool[])deviationMask.Clone()).AsMemory()));
            logger?.LogTrace(
                "DeviationMask (rotate180=False): masked={Masked} of {Total} px, fg pre={Pre} post={Post}.",
                maskedCount, n, /* pre */, survivorCount);
        }
        else
        {
            for (int i = 0; i < n; i++) if (deviationMask[i]) fg[i] = false;
        }
    }

    /* …existing morph-close + components + classify unchanged… */
}
```

A new `DetectionDiagnosticHooks.OnDeviationMask` member parallels the existing `OnRimMask`. A new `DeviationMaskSnapshot` record parallels `RimMaskSnapshot`. The `DeviationBlobCalibrationDetector.Detect` orchestrator at [`DeviationBlobCalibrationDetector.cs:44-71`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs#L44-L71) threads the new param via `DetectionRequest.DeviationMask`.

Mask representation is `bool[]` (matches the existing `fg` working buffer's shape) but is built from `GrayImage` (binary 0/255) by the engine — the conversion is cheap and one-shot. The engine-side mask production keeps `GrayImage` semantics for save-to-PNG observability; the detector consumes `bool[]` for inner-loop efficiency.

Trivial implementation cost; behavior preserved when `deviationMask == null`.

### 5.6 `MapCalibrationDetectorOptions` — new class, knobs + schema v1

Grep confirms `MapCalibrationDetectorOptions` does NOT exist today; this spec creates it. Modeled after `MapCalibrationLocateOptions` (existing schema-versioned settings pattern from #1061), wired via `AddMithrilVersionedSettings<MapCalibrationDetectorOptions>` in `DetectionServiceCollectionExtensions`.

```csharp
public sealed class MapCalibrationDetectorOptions : IVersionedState<MapCalibrationDetectorOptions>
{
    public bool   DeviationMaskingEnabled   { get; set; } = true;
    public int    BoundaryDilationPx        { get; set; } = 8;
    public bool   FogOfWarDetectionEnabled  { get; set; } = true;     // residual coverage
    public double FogVarianceThreshold      { get; set; } = 30.0;     // measured
    public byte   FogColorMin               { get; set; } = 110;
    public byte   FogColorMax               { get; set; } = 140;

    public int SchemaVersion { get; set; } = 1;
    public static int Version => 1;
    public static MapCalibrationDetectorOptions Migrate(MapCalibrationDetectorOptions loaded) => loaded;
}
```

Persists to `map-calibration-detector.json` next to the existing `map-calibration-locate.json`.

### 5.7 `AttemptJson` schema bump (additive `DeviationMask` file ref)

Existing record at [`CalibrationBundleJson.cs:6-23`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs#L6-L23) carries `SchemaVersion`. Bundles read today are at v3 (verified in `01-attempt.json:2`). Bump to v4 with an additive field on `AttemptFilesJson`:

```csharp
public sealed record AttemptFilesJson(
    /* …existing fields… */
    string? DeviationMask = null);   // NEW — schema v3 reader treats absent as null
```

`AttemptJson.SchemaVersion = 4`. `LocatorBestJson` and `SynthesisJson` are unchanged. Reader treats absence of `DeviationMask` as v3 (null).

### 5.8 Telemetry

**New span** `calibration.detect.mask` emitted from `AutoCalibrationEngine` around the mask-build sequence, on the Capture-layer `MithrilActivitySources.MapCalibration` catalog (mirrors `calibration.capture` / `calibration.refine` / `calibration.solve` peers seen at [`AutoCalibrationEngine.cs:499 / 551 / 706`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L499)). The `calibration.refine.fallback` span lives inside `CompositeMapRegionRefiner` and wraps the locator — wrong scope for mask tags.

Tags on the new span:

- `mask.boundary.available` (bool) — did the boundary mask compute successfully
- `mask.boundary.degenerate` (bool) — was the resulting mask empty (all-0 or all-1)
- `mask.fog.available` (bool) — same for fog mask
- `mask.coverage` (double) — fraction of recovered-region pixels in the combined mask

Update `docs/perf-trace-schema.md` with the new span + tag vocabulary. Update `tests/Mithril.Shared.Tests/PerfTracerTests.cs` byte-parity test.

### 5.9 Logging

Per CLAUDE.md "Instrumentation is the contract." New logger category `"Mithril.MapCalibration.Detection.Mask"` for the mask computation steps. Lifecycle `LogInformation` per attempt:

```
[Information] Mithril.MapCalibration.Detection.Mask:
  Mask applied — boundary=8px dilation (cache hit), fog=12.3% coverage, combined=18.7% coverage.
```

Per-stage `LogTrace`:
- Floor boundary mask cache hit / miss + size + dilation
- Fog detector decision + thresholds
- Mask combiner output coverage

`LogWarning` on safe-degrade paths (alpha unavailable, mask degenerate, fog detector exception).

## 6. Persistence — bundle + settings round-trip

**Bundle:** `AttemptJson.SchemaVersion` v3 → v4 with additive `AttemptFilesJson.DeviationMask` file ref. New `07a-deviation-mask.png` written by `AutoCalibrationEngine` alongside the existing `07c-rim-mask.png` (which it writes via the `OnRimMask` diagnostic hook). PNG is 8-bit grayscale (0 = include in deviation, 255 = mask out), same dimensions as the deviation map.

**Settings:** `MapCalibrationDetectorOptions` persists as `map-calibration-detector.json` via `AddMithrilVersionedSettings<MapCalibrationDetectorOptions>`. Schema v1 with identity Migrate.

## 7. Error handling

| Failure | Behavior |
|---|---|
| `TryGetTextureAlpha` returns `null` | `LogWarning`, span tag `mask.boundary.available=false`. Floor mask = null; fog mask only. Don't fail attempt. |
| Alpha decode succeeds but mask is degenerate (all 0 or all 1) | `LogWarning`, span tag `mask.boundary.degenerate=true`. Floor mask = null; fog mask only. |
| `FogOfWarDetector.Detect` throws | Catch, `LogWarning`, span tag `mask.fog.available=false`. Fog mask = null; boundary mask only. |
| Both masks unavailable | `LogInformation`. Deviation step runs unmasked (current pre-fix behavior). #1116 not fixed for this attempt; nothing regresses. |
| `DeviationMaskingEnabled=false` | Skip both mask computations entirely. Behavior byte-identical to pre-fix. Behind-flag escape hatch. |

No new exceptional paths. Every "couldn't mask" case → degrade to weaker masking → still produce a result; the existing solver gate decides accept/reject.

## 8. Testing strategy

| Test | Project | Asserts |
|---|---|---|
| `FloorBoundaryMaskCache_caches_per_area_key` | `Mithril.MapCalibration.Tests` | Two calls with same `mapAssetKey` return same `GrayImage` reference. Different keys recompute. |
| `FloorBoundaryMaskCache_traces_alpha_edge_with_dilation` | same | Synthetic 100×100 alpha (filled rectangle), boundary mask traces rect edge with configured dilation. Interior pixels not masked. |
| `FloorBoundaryMaskCache_handles_degenerate_alpha` | same | All-0 and all-255 alpha → null mask + warning logged. No exception. |
| `FloorBoundaryMaskCache_handles_missing_alpha_provider` | same | `TryGetTextureAlpha` returns null → null mask + warning. |
| `FogOfWarDetector_detects_uniform_low_variance_region` | same | Synthetic image: half "fog" (constant grey 125) + half "floor detail". Detector marks the fog half. |
| `FogOfWarDetector_rejects_uniform_non_fog_bright_region` | same | Uniform grey 200 → no fog (above luminance window). |
| `FogOfWarDetector_rejects_high_variance_grey_region` | same | Noisy grey 125 → no fog (high variance). |
| `DeviationMaskCombiner_OR_combines` | same | Floor pixel = 1 → combined = 1. Fog pixel = 1 → combined = 1. Both 0 → combined 0. |
| `DeviationMaskCombiner_handles_null_inputs` | same | (null, fog) → fog. (floor, null) → floor. (null, null) → null. |
| `DeviationStep_applies_mask` | same | Mat with mixed below/above-threshold + mask covering half. Masked pixels → 0; unmasked → standard threshold. |
| `Hogans_corpus_mega_blob_shrinks_with_mask` | same | **Corpus test** — load Hogan's 091533 fixture (screenshot + gray texture + alpha texture), run masked detector pipeline. Assert Structure-class mega-blob area drops from 119,655 → ≤ `MegaBlobAreaCeiling`. Target ≤ 30,000 (TBD by measurement). |
| `Hogans_corpus_icon_count_rises_with_mask` | same | Same fixture. Assert Icon-class blob count rises from ~50 to ≥ `IconCountFloor`. Target ≥ 60 (TBD). |
| `TopFloor_corpus_no_fog_works` | same | TopFloor 095806 fixture (no fog-of-war). Assert (a) boundary mask cleanly traces alpha edges, (b) fog detector doesn't false-positive (`mask.fog.coverage < 5 %`), (c) detection map has cleaner Icon distribution than pre-fix. |
| `Eltibule_outdoor_corpus_doesnt_regress` | same | Eltibule fixture (goes through ORB primary). Assert algorithm = `"orb-lowe"`, masking ran without exception, recovered cal matches pre-fix within ≤ 1 px tolerance. |
| `MapCalibrationDetectorOptions_v1_round_trip` | `Mithril.MapCalibration.Capture.Tests` | Deserialize a v0 settings (absent file or empty defaults) → migrates to v1 with defaults. Round-trip a v1 settings, byte-identical. |
| `LocatorBestJson_v3_to_v4_round_trip_with_deviationMask` | same | Old v3 bundle without `deviationMask` field reads as null. New v4 bundle round-trips with the field. |

Corpus fixtures (PNGs + alpha PNGs) checked into `tests/Mithril.MapCalibration.Tests/Detection/boundary_mask_corpus/`. **Alpha-texture fixtures depend on the sidecar prereq landing** — the screenshot + gray texture are already extractable; the alpha texture is a new artifact the prereq spec produces.

## 9. Files touched (anticipated)

### 9.1 `src/`

| File | Change |
|---|---|
| `src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs` | Add `GrayImage? TryGetTextureAlpha(string mapAssetKey)` method to interface. (Sidecar prereq.) |
| `src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs` | Implement `TryGetTextureAlpha` by reading `map-texture-<area>-alpha.{json,bin}` from the cache dir; mirror existing SHA-256 + canonical-hash-gate verify path. (Sidecar prereq.) |
| `src/Mithril.MapCalibration.Detection/Internal/FloorBoundaryMaskCache.cs` | **new** — per §5.2. |
| `src/Mithril.MapCalibration.Detection/Internal/FogOfWarDetector.cs` | **new** — per §5.3. Residual-coverage component. |
| `src/Mithril.MapCalibration.Detection/Internal/DeviationMaskCombiner.cs` | **new** — per §5.4. |
| `src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs` | Add optional `bool[]? deviationMask` parameter to `DetectIconBlobs`. Implement the new subtract block between rim subtract and morph-close (current lines 164 → 174). Add `OnDeviationMask` diagnostic hook + `DeviationMaskSnapshot` record (parallel to `OnRimMask` / `RimMaskSnapshot`). |
| `src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs` | Thread `request.DeviationMask` (new field) into the `DetectIconBlobs` call at line 67. |
| `src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs` (or wherever `DetectionRequest` lives) | Add `GrayImage? DeviationMask` field to `DetectionRequest`. |
| `src/Mithril.MapCalibration.Detection/MapCalibrationDetectorOptions.cs` | **new** — per §5.6. New `Mithril.MapCalibration.Detection`-scoped options class. |
| `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` | Wire mask computation chain (boundary cache + fog detector + combiner); thread mask into `DetectionRequest`; write `07a-deviation-mask.png` artifact. |
| `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs` | Bump `AttemptJson.SchemaVersion` from 3 → 4. Add `DeviationMask` optional field to `AttemptFilesJson`. |
| `src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs` | Register `FloorBoundaryMaskCache`, `FogOfWarDetector`, `DeviationMaskCombiner` as singletons; register `MapCalibrationDetectorOptions` via `AddMithrilVersionedSettings`. |

### 9.2 `tests/`

| File | Change |
|---|---|
| `tests/Mithril.MapCalibration.Tests/Detection/FloorBoundaryMaskCacheTests.cs` | **new** — 4 unit tests. |
| `tests/Mithril.MapCalibration.Tests/Detection/FogOfWarDetectorTests.cs` | **new** — 3 unit tests. |
| `tests/Mithril.MapCalibration.Tests/Detection/DeviationMaskCombinerTests.cs` | **new** — 2 unit tests. |
| `tests/Mithril.MapCalibration.Tests/Detection/DeviationStepMaskTests.cs` | **new** — 1 integration test. |
| `tests/Mithril.MapCalibration.Tests/Detection/BoundaryMaskCorpusTests.cs` | **new** — 4 corpus tests (Hogan's mega-blob shrinks + icon count rises, TopFloor no-fog, Eltibule outdoor regression). |
| `tests/Mithril.MapCalibration.Tests/Detection/boundary_mask_corpus/` | **new** — fixture PNGs + alpha PNGs. Alpha fixtures land after sidecar prereq. |
| `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/LocatorBestJsonV4Tests.cs` (or equivalent) | **new** — round-trip test for additive `deviationMask` field. |
| `tests/Mithril.MapCalibration.Tests/MapCalibrationDetectorOptionsTests.cs` (or equivalent) | **new** — Migrate test. |

### 9.3 `docs/`

- This `spec.md` + sibling `plan.md` (slug: `map-calibration-deviation-mask-1116`).
- Sibling prereq spec at [`docs/planning/sidecar-rgba-alpha-surface/spec.md`](../sidecar-rgba-alpha-surface/spec.md).
- Append two rows to [`docs/planning/INDEX.md`](../INDEX.md).
- Update `docs/perf-trace-schema.md` — add the `mask.*` tags to the `calibration.refine.fallback` row.

### 9.4 Wiki (after PR merges)

- Update [Auto-Calibration Sub-Zone Findings](https://github.com/moumantai-gg/mithril/wiki/Auto-Calibration-Sub-Zone-Findings) — Mode-A mechanism re-framed (content-mismatch + masking gap, not locator precision), with the 4-sample locator-residual measurement bounds + the 5-bundle corpus signature.

## 10. Out of scope

- **Mode-B solver-side rejection** ([Goblin Dungeon main 095904 territory](https://github.com/moumantai-gg/mithril/issues/1116)). Clean 07b but solver rejects on "no geometrically-consistent fit." This spec doesn't touch the solver. Belongs in a separate sub-issue under #1116.
- **Locator's ≤3 px Y-anisotropic-scale residual.** Real, measured (four eyeball overlays on Hogan's 091533), but not load-bearing for the 200+ px symptom. File as separate locator follow-up sub-issue under #1116.
- **Synthesis-J Shadow → Enabled promotion** ([mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) path-1). The mask should make J score above `jMin` for Mode-A scenes — that's an indirect positive externality, not this spec's job to gate on.
- **The locator itself.** No changes to `SobelPaddedPyramidRefiner`, `FeatureMatchingRefiner`, `CompositeMapRegionRefiner`, or any pyramid/scale logic.
- **The morph-close, blob-classify, or solver code paths.** Their behavior is preserved; only the deviation map input changes.
- **Per-area mask overrides.** v1 ships one global `BoundaryDilationPx`; per-area tuning is a future possibility if a particular scene shows it's load-bearing.
- **Auto-cal trigger / replay paths.** [mithril#1130](https://github.com/moumantai-gg/mithril/pull/1130) closed the replay-contamination bug; nothing else in those paths is in scope.

## 11. Verification owed

| Claim | How to verify |
|---|---|
| Source PG textures actually carry usable alpha (opaque floor / transparent not-floor). | Sidecar prereq's Plan Task 0: sample 3-5 area textures, inspect alpha distribution. If alpha is NOT the floor/not-floor signal, this spec's D3 inverts and we fall to luminance heuristic (option 2 in brainstorm). |
| The user's 2026-06-12 GIMP overlay observation of "holes left by fog of war" in `07b-foreground.png` — given that [`LocalNccDeviation`'s `addedOnly:true` mode](../../../src/Mithril.MapCalibration.Detection/LocalNccDeviation.cs#L33-L42) is supposed to suppress fog-interior pixels as "the correct fog discriminator," do the user-observed fog holes correspond to fog INTERIOR (would indicate `addedOnly` isn't actually working) or fog EDGES (would be expected residual that `FogOfWarDetector` covers)? | Plan Task: re-overlay 07b on 06 for Hogan's 091533 with the fog-of-war regions explicitly annotated. If interior, file an `addedOnly`-bug sub-issue separately; the deviation mask spec ships anyway because the boundary mask alone closes the dominant component. If edges, the spec's residual-coverage justification for `FogOfWarDetector` is correct. |
| `BoundaryDilationPx = 8` is the right default. | Plan Task: corpus measurement on Hogan's 091533 — sweep dilation 2/4/6/8/10/12 px, measure (mega-blob area, icon count) Pareto. Pick dilation that minimizes mega-blob while leaving ≥60 Icons. |
| Fog detector's `FogVarianceThreshold` and color window are tuned correctly. | Plan Task: corpus measurement on Hogan's 091533 (with fog) + TopFloor 095806 (without fog) — sweep variance threshold + color window, look for the combination where Hogan's fog coverage matches the user-observed fog regions AND TopFloor fog coverage is < 5 %. |
| The fix doesn't regress on outdoor scenes (ORB primary). | Corpus test `Eltibule_outdoor_corpus_doesnt_regress`. Recovered cal matches pre-fix within ≤ 1 px tolerance. |
| Synthesis-J rises post-fix on Mode-A scenes (positive externality). | Manual smoke after impl: re-run Hogan's 091533 + TopFloor 095806; check `01-attempt.json.synthesis.j` — expect ≥ 8 (vs pre-fix 3.25). Document on wiki Findings if positive. |
| Mode-B failures (clean 07b, solver rejects) are NOT addressed. | Manual smoke: re-run Goblin Dungeon main 095904 — expect `outcome = rejected-solve` to persist (the mask didn't help; correct). Confirm a separate sub-issue exists. |

## 12. Cross-references

- [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) — this spec's home issue (umbrella). Mode-A path closes here; Mode-B remains open via a child sub-issue.
- [sidecar-rgba-alpha-surface spec](../sidecar-rgba-alpha-surface/spec.md) — **hard prereq.**
- [mithril#1070](https://github.com/moumantai-gg/mithril/issues/1070) (shipped [#1132](https://github.com/moumantai-gg/mithril/pull/1132)) — locator-side blur-aware-template fix; this spec sits at a different pipeline layer.
- [mithril#1061](https://github.com/moumantai-gg/mithril/issues/1061) (shipped [#1071](https://github.com/moumantai-gg/mithril/pull/1071)) — sparse-locate fallback this spec sits downstream of.
- [mithril#1117](https://github.com/moumantai-gg/mithril/pull/1118) (shipped) — synthesis-J shadow-mode observability; the `J=3.25` reading depends on this.
- [mithril#1123](https://github.com/moumantai-gg/mithril/pull/1124) (shipped) — detector-pipeline observability; `07b-foreground.png` and `10c-blob-pipeline.json` are the load-bearing diagnostic surfaces.
- [mithril#931](https://github.com/moumantai-gg/mithril/issues/931) (shipped [#932](https://github.com/moumantai-gg/mithril/pull/932)) — out-of-process asset sidecar; the prereq spec extends this.
- [Auto-Calibration Sub-Zone Findings (wiki)](https://github.com/moumantai-gg/mithril/wiki/Auto-Calibration-Sub-Zone-Findings) — corpus + mechanism notebook; updated after PR lands.
- [`docs/planning/calibration-pipeline-observability-1123/spec.md`](../calibration-pipeline-observability-1123/spec.md) — structural template for this spec.
- [`docs/perf-trace-schema.md`](../../perf-trace-schema.md) — telemetry shape contract; the `mask.*` tag additions land here.
