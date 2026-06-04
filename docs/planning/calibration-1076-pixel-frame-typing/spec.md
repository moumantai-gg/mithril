# Pixel-frame typing across the calibration → overlay → rendering pipeline

| | |
|---|---|
| **Catalyst issue** | [#1076](https://github.com/moumantai-gg/mithril/issues/1076) — drift check fails 0/7 on real captures because crop-space detections are compared against full-frame predictions. |
| **Related** | [#1075](https://github.com/moumantai-gg/mithril/issues/1075) (HashGate `pgVersion` plumbing — independent, cosmetic-only on the same attempt). Built on top of [#1046 / PR #1064](https://github.com/moumantai-gg/mithril/pull/1064) (which introduced the drift-check comparison block). |
| **Status** | active |

## 1 — Goal & non-goals

**Goal.** Make it impossible at compile time to compare, distance, or arithmetic-mix pixel coordinates that live in different frames anywhere in the calibration → overlay → rendering pipeline. Lift `AreaCalibration`'s implicit frame-overloading — today the same struct's `WorldToWindow` outputs *base-texture* pixels in the Capture path and *overlay-window* pixels in the Legolas path, with no type-level distinction — into the type system as well.

The catalyst bug ([#1076](https://github.com/moumantai-gg/mithril/issues/1076)) is one instance of the class. The intent of this spec is to close the class, not just the one site. The two unit-test fixtures that exercise the buggy comparison both pre-cropped their inputs, collapsing `loc.Tx/Ty` to ~0 — i.e. the bug only surfaces when the two implicit frames diverge, which means today's unit-test discipline cannot catch it. Type discipline can.

**In scope.**

- `Mithril.MapCalibration` (core: `PixelPoint`, `WorldCoord`, `AreaCalibration`, `MapRect`, `CandidateTransform`, `LandmarkCalibrationSolver`, `IMapCalibrationService`).
- `Mithril.MapCalibration.Detection` (`TypedDetection`, `TypeAwareRansacSolver`, refiners).
- `Mithril.MapCalibration.Capture` (`AutoCalibrationEngine`, diagnostics).
- `Mithril.Overlay` (`IOverlaySceneContext`, `MarkerSceneRenderer`, `OverlayWindowService`, `IWorldOverlayMarkers`).
- `Legolas.Module` (every consumer of `PixelPoint`: views, view models, services, rendering drawers, hotkeys, domain types).
- Test fixtures and tests in all of the above.
- The `AreaCalibration` JSON persistence schema (`SchemaVersion 1 → 2`).

**Out of scope.**

- A separately-mergeable hot-patch for #1076. No release has been cut; the bug is closed naturally by PR 3 of this refactor (the Detection + Capture migration). #1076 stays open until then.
- 3D rendering / Z-axis behaviour on pixel frames. Z is *carried* on every pixel struct (uniform shape with `WorldCoord`) but always 0 today; no consumer reads it.
- World↔world rotations or area-to-area transforms.
- The `#931` HashGate `pgVersion` plumbing ([#1075](https://github.com/moumantai-gg/mithril/issues/1075)) — independent issue, unchanged by this work.
- Telemetry / span tagging by frame — possible follow-up; the typed structs make it trivial to add later, but not part of this landing.

## 2 — Problem statement

### 2.1 The catalyst bug

`AutoCalibrationEngine.CheckDriftCoreAsync` ([src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs:285-289](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs)) computes reference predictions in **full captured-frame** pixel coordinates:

```csharp
var predTex = stored.WorldToWindow(r.World, currentZoom: 1.0);
var predScreenX = predTex.X * loc.Scale + loc.Tx;
var predScreenY = predTex.Y * loc.Scale + loc.Ty;
```

But it compares them against `TypedDetection.AnchorX/AnchorY`, which the detector emits in **crop-local** pixel coordinates — the screenshot the detector consumed was `crop = ImageOps.Crop(gray, clamped.OriginX, clamped.OriginY, …)` ([AutoCalibrationEngine.cs:255](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs)). `TypedDetection`'s XML doc ([src/Mithril.MapCalibration.Detection/TypedDetection.cs:4-9](../../../src/Mithril.MapCalibration.Detection/TypedDetection.cs)) explicitly states "screenshot-pixel space" — and the screenshot here is the *crop*.

The mismatch is `(loc.Tx, loc.Ty)`. On Map_KhyruleksCrypt 2026-06-04 20:28:01 those values were `(320.1, 57.6)` — verified independently against the user's manual locate of the same frame, which produced `(326, 67)`. `DriftMatchGatePx = 20`, so every reference falls outside the gate → `0/7 matched` → "inconclusive, no arming."

### 2.2 The structural overload

`AreaCalibration` ([src/Mithril.MapCalibration/AreaCalibration.cs](../../../src/Mithril.MapCalibration/AreaCalibration.cs)) is constructed in two unrelated places with two unrelated meanings:

| Where | Solver | `OriginX/OriginY/Scale/Rotation` mean | `WorldToWindow` returns |
|---|---|---|---|
| `Mithril.MapCalibration.Capture` (AutoCalibration RANSAC solve) | RANSAC over the base texture | texture-pixel parameters | texture pixels |
| `Legolas.Module/Services/AreaCalibrationService` (wizard / manual placement) | Two- or N-point fit over the overlay window | overlay-pixel parameters | overlay pixels |

Persistence (`UserRefinementStore`, community-calibration JSON, the in-process `IMapCalibrationService`) carries the same struct across both meanings with no discriminator. A texture-frame calibration written by AutoCalibration and an overlay-frame calibration written by the Legolas wizard are indistinguishable on disk and in memory.

Pixel-frame structs alone do not close this hole. If `WorldToWindow` returns `PixelPoint` today and `TexturePixel` tomorrow, the same `AreaCalibration` instance still returns the wrong frame for callers expecting overlay pixels (or vice versa). The struct itself must carry its output-frame identity.

### 2.3 Why CI is green

The comparison block in §2.1 was added in PR #1064 (closed #1046). The drift-check tests at `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineDriftCheckTests.cs` feed pre-aligned synthetic inputs where the located rect originates at `(0, 0)` in the captured frame — so `loc.Tx/Ty ≈ 0` and full-frame and crop-frame coincide. The PR explicitly carried "verification owed" for in-game smoke; that smoke is what surfaced the bug.

## 3 — Frame enumeration

Six pixel frames; each gets its own concrete struct. `WorldCoord` (already a separate type) is the seventh frame, untouched.

| Frame | Origin | Used by | Current untyped form |
|---|---|---|---|
| **`TexturePixel`** | top-left of the canonical base-texture asset | AutoCalibration solver, drift check, RANSAC inputs, ORB descriptors | `predTex` in `AutoCalibrationEngine`; `e.Tx/e.Ty` arrays in `TypeAwareRansacSolver`; output of `AreaCalibration.WorldToWindow` *when calibration is texture-frame* |
| **`CapturedFramePixel`** | top-left of the full OS-captured frame | Locator outputs, drift-check comparisons (today incorrectly) | `loc.Tx/loc.Ty`; `predScreenX/predScreenY` in the bug site; `MapRect.OriginX/OriginY` when describing the located rect in full-frame coords |
| **`CroppedFramePixel`** | top-left of the located map crop inside the captured frame | `TypedDetection.AnchorX/Y`; detector input image space | `DetectionRequest.Screenshot` coords; `IconLikelihoodField` outputs |
| **`OverlayPixel`** | top-left of the Mithril overlay window | All Legolas rendering, marker drawers, `IWorldOverlayMarkers`, `IOverlaySceneContext.Project`, `AreaCalibration.WorldToWindow` *when calibration is overlay-frame* | Most `PixelPoint` uses in `Legolas.Module/Rendering/*` and `Mithril.Overlay/Internal/*` |
| **`CanvasPixel`** | top-left of a WPF Canvas (mouse events) | Wizard click handling, calibration-marker drag | `Mouse.GetPosition(canvas)` callers in `MapOverlayView`, `CalibrationOverlayView`, `OverlayController` |
| **`GameWindowPixel`** | top-left of the PG game window (DWM client area) | Capture-rect math ([#947](https://github.com/moumantai-gg/mithril/issues/947)) | Embedded in `MapCaptureRect` calculations; conversion to `CapturedFramePixel` is a translation by the rect's origin |

Judgement calls captured at design time:

- **`OverlayPixel` vs `CanvasPixel` kept separate** — at our current single-monitor DPI they're numerically identical for the markers/clicks we care about. Separating them is cheap insurance against per-monitor DPI surprises; conversion is a one-line identity helper today (`CanvasOverlayMapping.CanvasToOverlay` / `OverlayToCanvas`).
- **`GameWindowPixel` introduced** even though it only matters at the capture-rect persistence boundary. The persistence layer (#947) is where "what coordinate space am I in?" confusion has historically been most expensive.

## 4 — Core types

### 4.1 Shared interface (frame-erased access)

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// Unsafe / frame-erased read access to a pixel coordinate. Use ONLY at
/// well-defined leaf sites where the consumer is intrinsically frame-blind:
///   • Direct2D / WPF rendering primitives (the GPU doesn't care about frames)
///   • Serialisation (JSON / log formatting)
///   • Interop with third-party libraries that take raw doubles (OpenCvSharp)
/// Going through this interface erases frame identity — do not use it in any
/// code that combines coordinates from more than one source.
/// </summary>
public interface IPixelPoint
{
    double X { get; }
    double Y { get; }
    double Z { get; }
}
```

### 4.2 Concrete frame structs

One canonical example; the other five are identical-shaped (one file each):

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// A point in the canonical base-texture pixel frame: origin at the texture's
/// top-left, X right, Y down. Z is always 0 today; carried for symmetry with
/// <see cref="WorldCoord"/> and to keep the IPixelPoint shape uniform across
/// all frames.
/// </summary>
public readonly record struct TexturePixel(double X, double Y, double Z) : IPixelPoint
{
    public TexturePixel(double x, double y) : this(x, y, 0) { }
    public static TexturePixel Zero => new(0, 0, 0);

    public double DistanceTo(TexturePixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double DistanceSquaredTo(TexturePixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}
```

Identical shape for `CapturedFramePixel`, `CroppedFramePixel`, `OverlayPixel`, `CanvasPixel`, `GameWindowPixel`.

### 4.3 Z handling

- Three-component `(X, Y, Z)` primary constructor on every pixel struct.
- Two-component `(X, Y)` convenience constructor that defaults Z to 0.
- `IPixelPoint.Z` is part of the contract.
- `DistanceTo` / `DistanceSquaredTo` use 2D math (X/Y only) — matches today's behaviour. If a future feature lights up Z, we add `DistanceTo3D` rather than retrofitting the 2D semantics.
- Serialisation writes all three components.

### 4.4 Within-frame arithmetic

Within a single frame the struct supports `DistanceTo` / `DistanceSquaredTo`, equality, hash, and deconstruction. No `operator +` or `operator -` between two pixel points — they aren't vectors, and offsets between frames go through named conversion methods.

If a real consumer for within-frame vector math surfaces (drag deltas, pin-to-pin offsets, etc.), we add a sibling `PixelOffset<TFrame>` type then; not speculatively now.

## 5 — Cross-frame conversion

Every conversion is a method on the value that **owns the relationship** — a `MapRect` knows its crop origin, a `MapCaptureRect` knows the game window's position. There are no free-floating conversion helpers. This puts the question "where does my frame transform come from?" on the call site, by construction.

| Conversion | Lives on | Method name |
|---|---|---|
| `WorldCoord → TexturePixel` | `WorldToTextureCalibration` (new, see §6) | `ToTexture(WorldCoord, double currentZoom)` |
| `TexturePixel → WorldCoord` | `WorldToTextureCalibration` | `FromTexture(TexturePixel, double currentZoom)` |
| `WorldCoord → OverlayPixel` | `WorldToOverlayCalibration` (new, see §6) | `ToOverlay(WorldCoord, double currentZoom)` |
| `OverlayPixel → WorldCoord` | `WorldToOverlayCalibration` | `FromOverlay(OverlayPixel, double currentZoom)` |
| `TexturePixel → CroppedFramePixel` | `MapRect` (existing struct, frame restricted — see §5.1) | `TextureToCropped(TexturePixel)` |
| `CroppedFramePixel → TexturePixel` | `MapRect` | `CroppedToTexture(CroppedFramePixel)` |
| `CroppedFramePixel → CapturedFramePixel` | `LocatedMapRect` (new wrapper, see §5.1) | `CroppedToCaptured(CroppedFramePixel)` |
| `CapturedFramePixel → CroppedFramePixel` | `LocatedMapRect` | `CapturedToCropped(CapturedFramePixel)` |
| `GameWindowPixel ↔ CapturedFramePixel` | `MapCaptureRect` (existing) | `GameWindowToCaptured` / `CapturedToGameWindow` |
| `CanvasPixel ↔ OverlayPixel` | new `CanvasOverlayMapping(double dpiScale)` value type | `CanvasToOverlay` / `OverlayToCanvas` (identity at DPI=1) |

`AreaCalibration` cross-frame: see §6.

### 5.1 `MapRect` frame restriction + `LocatedMapRect`

`MapRect` today carries `(OriginX, OriginY, Width, Height, TextureWidth, TextureHeight)` where `(OriginX, OriginY)` is "the canonical region's origin within the screenshot the detector consumes." The screenshot can be the cropped frame (origin always `(0, 0)` by construction — the `alignedRect` case in `AutoCalibrationEngine.cs:257`) **or** the full captured frame (origin is the located rect's position). Same struct, two implicit frame interpretations — and the existing `MapRect.ScreenshotToTexture(double, double)` API takes raw doubles, so the choice leaks across call sites.

Disambiguation:

- **`MapRect`** keeps its existing shape but its `(OriginX, OriginY)` is constrained to be in the **screenshot's own frame** (i.e. `(0, 0)` for crop-aligned cases — every existing in-tree construction site fits this). Its typed conversions are `TextureToCropped` / `CroppedToTexture` only — purely texture↔crop, no captured-frame relationship.
- **`LocatedMapRect(MapRect MapRect, CapturedFramePixel Origin)`** (new) carries the located rect's placement within the captured frame. Its conversions are `CroppedToCaptured` / `CapturedToCropped`, and it exposes the inner `MapRect` for the texture↔crop side.

Per the §13 verification-owed audit (`MapRect` construction-site bucketing), each of the current ~5 `MapRect` construction sites is reclassified into "bare `MapRect`" or "`LocatedMapRect`" at PR-1 time. If any site genuinely needs both meanings, that's the signal to split `MapRect` further; the audit will surface it.

## 6 — `AreaCalibration` split

### 6.1 Two structs, one private math core

```csharp
namespace Mithril.MapCalibration;

/// <summary>World → base-texture-pixel projection. Owned by Capture/Detection.</summary>
public readonly record struct WorldToTextureCalibration(
    double OriginX,        // texture-pixel
    double OriginY,        // texture-pixel
    double Scale,          // texture-pixels per world-metre
    double RotationRadians,
    bool MirrorNorth,
    double CalibrationZoom)
{
    public int SchemaVersion { get; init; } = 1;

    public TexturePixel ToTexture(WorldCoord world, double currentZoom) =>
        AreaProjectionCore.Project(OriginX, OriginY, Scale, RotationRadians,
            MirrorNorth, CalibrationZoom, world, currentZoom) is var (x, y)
                ? new TexturePixel(x, y) : default;

    public TexturePixel ToTexture(WorldCoord world) => ToTexture(world, CalibrationZoom);

    public WorldCoord? FromTexture(TexturePixel pixel, double currentZoom) =>
        AreaProjectionCore.Unproject(OriginX, OriginY, Scale, RotationRadians,
            MirrorNorth, CalibrationZoom, pixel.X, pixel.Y, currentZoom);

    public WorldCoord? FromTexture(TexturePixel pixel) => FromTexture(pixel, CalibrationZoom);
}

/// <summary>World → overlay-pixel projection. Owned by Mithril.Overlay / Legolas.</summary>
public readonly record struct WorldToOverlayCalibration(
    double OriginX,        // overlay-pixel
    double OriginY,        // overlay-pixel
    double Scale,          // overlay-pixels per world-metre
    double RotationRadians,
    bool MirrorNorth,
    double CalibrationZoom)
{
    public int SchemaVersion { get; init; } = 1;

    public OverlayPixel ToOverlay(WorldCoord world, double currentZoom) { /* delegate to AreaProjectionCore */ }
    public OverlayPixel ToOverlay(WorldCoord world) => ToOverlay(world, CalibrationZoom);

    public WorldCoord? FromOverlay(OverlayPixel pixel, double currentZoom) { /* delegate */ }
    public WorldCoord? FromOverlay(OverlayPixel pixel) => FromOverlay(pixel, CalibrationZoom);
}
```

The math is identical. `AreaProjectionCore` is a `file`-scoped or `internal` static class holding the rotation+scale+mirror+zoom arithmetic on raw doubles; both wrappers delegate. This gives one source of truth for the math without records-with-inheritance awkwardness.

### 6.2 Texture↔overlay calibration bridge

Exactly one transform legitimately crosses the texture/overlay boundary: rendering an AutoCalibration-derived calibration into the Legolas overlay. That goes through one explicit named method:

```csharp
// On WorldToTextureCalibration:
public WorldToOverlayCalibration ProjectThroughOverlay(MapRect overlayRect) { ... }
```

`overlayRect` carries the texture-origin and overlay-scale of where the base texture renders on the overlay window. The output `WorldToOverlayCalibration` composes the texture-frame projection with the texture→overlay placement.

Today this composition is implicit in how the overlay consumes a texture-calibration record. Lifting it to a single named method confines the texture/overlay relationship to one auditable spot.

### 6.3 `IMapCalibrationService` API change

Today (frame-overloaded):

```csharp
PixelPoint? WorldToWindow(MapSceneRef scene, WorldCoord world, double currentZoom);
WorldCoord? WindowToWorld(MapSceneRef scene, PixelPoint pixel, double currentZoom);
```

After:

```csharp
TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom);
WorldCoord? TextureToWorld(MapSceneRef scene, TexturePixel pixel, double currentZoom);

OverlayPixel? WorldToOverlay(MapSceneRef scene, WorldCoord world, double currentZoom);
WorldCoord? OverlayToWorld(MapSceneRef scene, OverlayPixel pixel, double currentZoom);
```

Callers pick the frame they want at the call site. The picker (`MapCalibrationService.GetCalibration`) returns either `WorldToTextureCalibration?` or `WorldToOverlayCalibration?` depending on which method invoked it; the picker logic from PR #1064 (residual + min-ref-count gate + source-precedence tiebreak) is unchanged in semantics — only the typed slice it operates over differs.

**Behaviour when the requested frame isn't available.** Each method returns `null` when no calibration of the requested frame exists for the scene (e.g. caller asks for `WorldToOverlay` but the store holds only `WorldToTextureCalibration` records). Callers that need a frame they don't have route through `WorldToTextureCalibration.ProjectThroughOverlay(MapRect)` (§6.2) explicitly — the service does NOT silently cross-frame-project on the caller's behalf, because that requires a `MapRect` the service doesn't own.

### 6.4 Internal storage

```csharp
internal sealed class MapCalibrationService : IMapCalibrationService
{
    private readonly Dictionary<MapSceneRef, IReadOnlyList<WorldToTextureCalibration>> _textureRecords;
    private readonly Dictionary<MapSceneRef, IReadOnlyList<WorldToOverlayCalibration>> _overlayRecords;
    // ...
}
```

`UserRefinementStore`, the community-calibration loader, and the baseline loader each tag their writes with the source-implied frame (see §7) and route into the appropriate list.

## 7 — Persistence

### 7.1 Schema bump

`AreaCalibration` JSON `SchemaVersion` advances 1 → 2 with one additive field:

```json
{
  "schemaVersion": 2,
  "frame": "Texture" | "Overlay",
  "originX": …,
  "originY": …,
  "scale": …,
  "rotationRadians": …,
  "mirrorNorth": …,
  "calibrationZoom": …,
  "source": "UserRefinement" | "AutoCapture" | "CommunitySync" | "BundledBaseline",
  "residualPixels": …,
  "referenceCount": …
}
```

### 7.2 Load-time provenance fallback (Schema 1 → 2)

Schema-1 records do not carry `frame`. Inferred at load by `source`:

| `Source` | Inferred frame |
|---|---|
| `UserRefinement` | Texture *if* the record was written by `AutoCalibrationEngine`'s refinement path; Overlay *if* written by `Legolas.Module/Services/AreaCalibrationService`. The two paths historically write to **separate JSON files**: AutoCalibration → `UserRefinementStore` under `MapCalibration/`, Legolas → `LegolasSettings.AreaCalibrations`. Inference is by file-of-origin, not by `Source` value. — **Verification owed: confirm Legolas writes never landed in `UserRefinementStore`.** |
| `AutoCapture` | Texture |
| `CommunitySync` | Texture (community-calibration repo `mithril-calibration/main/aggregated/*.json` ships only AutoCalibration-emitted records). — **Verification owed: spot-check 3+ aggregated files for `Source: AutoCapture` exclusively.** |
| `BundledBaseline` | Texture |

Fresh writes (Schema 2) always include `"frame"` explicitly. An unknown `"source"` (forward-compat) defaults to Texture with a one-time warn-log; the caller's frame demand catches a mismatch at API surface.

### 7.3 Forward compatibility

A Schema-2 record loaded by a pre-refactor Mithril build ignores the unrecognised `"frame"` field and loads as before (additive change). A Schema-1 record loaded by a post-refactor build runs the §7.2 inference. Downgrades aren't anticipated, but the additivity is free safety.

## 8 — What this rules out at compile time

The exact bug from #1076 — comparing `predScreenX` against `d.AnchorX` across frames — becomes:

```csharp
TexturePixel predTex = stored.ToTexture(r.World, currentZoom: 1.0);
CroppedFramePixel det = detection.Anchor;
var dist = predTex.DistanceTo(det);  // ❌ doesn't compile
```

The author is forced to write:

```csharp
var dist = predTex.DistanceTo(mapRect.CroppedToTexture(det));
// or equivalently
var dist = mapRect.TextureToCropped(predTex).DistanceTo(det);
```

…at which point the question "which frame am I measuring in?" has to be answered explicitly. The same protection extends to:

- Detection arrays folded into RANSAC solvers.
- Overlay marker positions accidentally using texture-frame inputs.
- Locator outputs whose `tx/ty` get confused with anything in a different frame.
- A Legolas-wizard-produced calibration fed to AutoCalibration's drift check. The wizard returns `WorldToOverlayCalibration`; the drift check signature demands `WorldToTextureCalibration`. Doesn't compile.
- Rendering an AutoCalibration record onto the overlay without going through `ProjectThroughOverlay(MapRect)`. Doesn't compile.

## 9 — Migration phasing

Seven sequential PRs. Each PR ends with a coherent codebase state and green CI. After PR 3 the bug class from #1076 is dead at compile time.

Adopted because the project's collaboration rules require branch + PR (no direct commits to main), and the squash-merge orphans add-then-delete patterns at the ~90-day mark — so add-only PRs followed by a later remove-only PR is the safe ordering.

| PR | What lands | Net file impact | Closes |
|---|---|---|---|
| **1. New types alongside** | 6 pixel structs, `IPixelPoint`, `WorldToTextureCalibration`, `WorldToOverlayCalibration`, `AreaProjectionCore`, `MapRect` typed conversion methods (`TextureToCropped` / `CroppedToTexture`), `LocatedMapRect` (§5.1), `MapCaptureRect.GameWindowToCaptured`, `CanvasOverlayMapping`. Tests for new types only. Old `PixelPoint` / `AreaCalibration` / `IMapCalibrationService` untouched. | ~13 src, ~7 tests | — |
| **2. Migrate `Mithril.MapCalibration` core** | `LandmarkCalibrationSolver.Reference`, `CandidateTransform`, internal `MapCalibrationService` switch to new types. `IMapCalibrationService` grows new methods; old `WorldToWindow` / `WindowToWorld` get `[Obsolete]` shims that delegate. | ~12 src, ~15 tests | — |
| **3. Migrate Detection + Capture** | `TypedDetection.Anchor`, `TypeAwareRansacSolver`, refiners (`SobelPaddedPyramidRefiner`, `FeatureMatchingRefiner`, `CompositeMapRegionRefiner`), `AutoCalibrationEngine`, diagnostics. Drift-check comparison naturally compiles correct after this — the old crop/full-frame mix-up cannot recur. | ~15 src, ~20 tests | **[#1076](https://github.com/moumantai-gg/mithril/issues/1076)** |
| **4. Migrate `Mithril.Overlay`** | `IOverlaySceneContext.Project`, `IWorldOverlayMarkers`, `MarkerSceneRenderer`, `OverlayWindowService` move to `OverlayPixel`. | ~6 src, ~3 tests | — |
| **5. Migrate `Legolas.Module`** | Views, view models, services (`CoordinateProjector`, `AdaptiveRouteOptimizer`, `AreaCalibrationService`, `MotherlodeMeasurementCoordinator`, `PinCalibrationCoordinator`, `MultilaterationSolver`), rendering drawers, hotkeys, domain types. Splits into 5a/5b along view-model boundary if review fatigue. | ~30 src, ~35 tests | — |
| **6. Persistence-schema migration** | `AreaCalibration` JSON SchemaVersion 1 → 2, add `frame` field with provenance fallback (§7.2). Round-trip tests cover old-format loads from all four `Source` values + fresh writes. Foldable into PR 1 if storage code is touched there too. | ~3 src, ~4 tests | — |
| **7. Tombstone PR** | Delete the obsolete `PixelPoint`, `AreaCalibration`, deprecated `IMapCalibrationService` methods. No remaining consumers. | -3 src, -1 test | — |

## 10 — Test strategy

**Per-PR baseline.** All existing tests stay green at every PR boundary. No PR ships with `[Skip]`s.

**Behavioural-equivalence tests** (PR 2). Assert the new typed math (`WorldToTextureCalibration.ToTexture`, `WorldToOverlayCalibration.ToOverlay`, and the `AreaProjectionCore` core) produces bit-identical results to the old `AreaCalibration.WorldToWindow` for a canonical fixture covering: identity transform, scale-only, rotation-only, mirror-only, zoom-aware, and combined. Same for the inverses.

**Regression marker for #1076** (PR 3). One drift-check fixture where `loc.Tx/Ty` is non-zero — i.e. the located rect originates somewhere other than (0, 0) in the captured frame. Today's buggy comparison would have failed this fixture; the typed comparison passes. Functions as both a regression test for #1076 and an exemplar for future drift-check authors.

**Persistence round-trips** (PR 6).

- Write a Schema-2 record, read it back, assert field-by-field equality across all fields including `frame`.
- Load a Schema-1 record from each of the four `Source` values and assert the inferred frame matches §7.2.
- Load a Schema-1 record with an invalid `Source` (forward-compat for unknown sources) and assert the safe Texture default plus the warn-log.
- File-of-origin disambiguation for `Source: UserRefinement` — a `UserRefinementStore`-rooted record loads as Texture; a `LegolasSettings.AreaCalibrations`-rooted record loads as Overlay.

**Type-system shape tests** (PR 1). Per-frame: `default(TexturePixel)` is `(X=0, Y=0, Z=0)`; the `(X,Y)` ctor sets Z to 0; equality / hash work; `IPixelPoint.X/Y/Z` reflects the concrete struct. Cheap insurance against silently breaking the struct shape later.

**Conversion identity tests** (PR 1). For each `MapRect` / `MapCaptureRect` / `CanvasOverlayMapping` conversion: `forward(backward(p)) == p` and `backward(forward(p)) == p` for a canonical fixture set covering origin-at-(0,0), origin-at-non-zero, identity-DPI, and non-identity-DPI cases.

## 11 — Risk surface

**Concurrent `PixelPoint` work.** 175 src + 308 test occurrences across 86 files. Anyone editing the same files during the migration sees nontrivial conflicts. Communicate before PR 5 starts (the longest chunk).

**Test fixtures with implicit-frame numeric constants.** Some Legolas tests bake in pixel coordinates whose frame is only known by the file's surrounding context. PR 5 audits these; a few will need fixture data re-stated in the now-explicit frame. — **Verification owed: spot-check 5+ Legolas test files to confirm the implicit-frame is recoverable.**

**Persistence forward-compat.** A Schema-2 record loaded by a pre-refactor Mithril build silently ignores `"frame"` (additive). A Schema-1 record loaded by a post-refactor build runs the §7.2 inference. Downgrades aren't anticipated; the additivity is free safety.

**`AreaCalibration` math port.** Two structs share one private `AreaProjectionCore` static. The PR 2 bit-identical equivalence tests are the safety net for the port; if those fail, the math diverged.

**`AreaCalibrationService` in Legolas writes its own records today.** After PR 6 it writes `frame: "Overlay"` explicitly; the inference-from-source-and-file logic only matters for records that landed pre-refactor. Verify by hand on a real LocalLow profile during PR 5 / 6.

**Per-monitor DPI surprises in `CanvasOverlayMapping`.** Today the conversion is identity. The single-DPI assumption holds at our current resolution but a future per-monitor-DPI consumer would need the mapping to do the real math. The type system catches it (you can't pass a `CanvasPixel` where an `OverlayPixel` is needed without going through `CanvasOverlayMapping`); the *math* in that mapping is the residual risk.

## 12 — Out of scope / follow-ups

- **`PixelOffset<TFrame>` type** if within-frame vector math (pin-to-pin offsets, drag deltas, route segments) becomes a real consumer need. Today the rendering code does manual `dx = a.X - b.X` and it's confined enough not to warrant a new type yet.
- **Real 3D pixel-Z consumers.** Z is carried-but-zero today. `DistanceTo3D` ships only when a 3D HUD / perspective overlay needs it.
- **Frame-tagged telemetry.** Spans on `MithrilActivitySources.MapCalibration` could carry the frame as a tag. Easy add post-refactor; not part of this landing.
- **DPI-aware `CanvasOverlayMapping`.** When per-monitor DPI support arrives, this is the one place that changes.
- **`#931` HashGate `pgVersion` plumbing** ([#1075](https://github.com/moumantai-gg/mithril/issues/1075)) — independent issue, unchanged by this work.

## 13 — Verification owed

Tracked here so they don't get lost as the spec turns into PRs.

- [ ] **§7.2 file-of-origin disambiguation for `Source: UserRefinement`** — confirm that Legolas-wizard-emitted overlay-frame records never landed in `UserRefinementStore`'s JSON path (only in `LegolasSettings.AreaCalibrations`). If they did, §7.2 needs a richer disambiguator than file-of-origin.
- [ ] **§7.2 community-calibration repo content** — spot-check 3+ files under `mithril-calibration/main/aggregated/` to confirm they ship `Source: AutoCapture` records only, and that the frame inference (texture) is correct.
- [ ] **§11 Legolas test fixture audit** — spot-check 5+ Legolas test files using hard-coded `PixelPoint` numeric values to confirm the implicit frame is recoverable from surrounding context. Surface any that aren't before PR 5 starts.
- [ ] **PR 3 in-game smoke** — re-run the original Map_KhyruleksCrypt scenario (manual calibrate hotkey, `loc.Tx/Ty ≈ (320, 58)`) and confirm the drift check returns either `Ok` or `Drift`-and-arm, but **not** `Inconclusive — too few visible landmarks`. Closes #1076's "verification owed" entry inherited from PR #1064.
- [ ] **`MapRect` construction-site bucketing** (§5.1) — audit each current `MapRect` construction site and classify into "bare `MapRect`" (screenshot = crop, origin always `(0, 0)`) or "needs `LocatedMapRect`" (origin in captured-frame coords). Five sites observed in the audit; the classification is load-bearing for §5.1's type restriction. If any site genuinely needs both meanings, that's the signal to split `MapRect` further.

---

*Drafted by Claude (Opus 4.7) during the 2026-06-04 / 2026-06-05 brainstorming session, posted by @arthur-conde.*
