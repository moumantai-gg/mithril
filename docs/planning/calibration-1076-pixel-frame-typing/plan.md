# Pixel-frame typing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the untyped `PixelPoint` / `AreaCalibration` pair with six concrete frame-tagged pixel structs and two frame-explicit calibration structs, so cross-frame arithmetic stops compiling and the [#1076](https://github.com/moumantai-gg/mithril/issues/1076) drift-check bug class is closed.

**Architecture:** Seven sequential PRs. PR 1 adds new types alongside the old ones (additive, no consumer change). PRs 2–5 migrate the consumers project-by-project (`Mithril.MapCalibration` core → Detection + Capture → Overlay → Legolas), each leaving a coherent green-CI codebase. PR 6 bumps the `AreaCalibration` JSON schema with a load-time provenance fallback. PR 7 deletes the obsolete types. After PR 3 the [#1076](https://github.com/moumantai-gg/mithril/issues/1076) bug class is dead at compile time; PRs 4–7 extend the protection outward and clean up.

**Tech Stack:** C# 13 / .NET 10 (net10-windows), record structs, xunit + FluentAssertions, MSBuild via `Mithril.slnx`, `System.Text.Json` with source-generated contexts, `gh` CLI for PR creation.

**Spec:** [`spec.md`](spec.md) in this folder. Sections referenced as **§N** throughout.

---

## Pre-flight — verify spec assumptions before PR 1

These are the four "Verification owed" entries from **§13** of the spec. Each must close before the dependent phase begins, because each can invalidate part of the design.

### Task P.1: Confirm `UserRefinementStore` never holds overlay-frame records

**Files:**
- Read-only: `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs`
- Read-only: `src/Legolas.Module/Services/AreaCalibrationService.cs`
- Read-only (sample): `%LocalAppData%/Mithril/MapCalibration/refinements.json`

- [ ] **Step 1: Read `UserRefinementStore` write path**
  Confirm the file root used for `Save` calls (expected: `<LocalAppData>/Mithril/MapCalibration/refinements.json` based on the live capture).

- [ ] **Step 2: Read `AreaCalibrationService` write path**
  Find every `Save`/`Write` call and confirm the destination is `LegolasSettings.AreaCalibrations` (in-process state persisted via `JsonSettingsStore<LegolasSettings>`), not `UserRefinementStore`.

- [ ] **Step 3: Grep for cross-pollination**
  ```bash
  rg -n "UserRefinementStore" src/Legolas.Module/
  rg -n "AreaCalibrationService" src/Mithril.MapCalibration/
  ```
  Expected: no calls from Legolas into `UserRefinementStore`, no calls from MapCalibration core into Legolas's `AreaCalibrationService`.

- [ ] **Step 4: Record outcome in spec**
  If clean → tick the verification box in `spec.md` §13. If a cross-call exists → STOP. The spec's §7.2 file-of-origin disambiguation is invalid; bring the finding back to brainstorming.

### Task P.2: Spot-check community-calibration repo aggregated files

**Files:**
- Read-only (sample): three files under `https://github.com/moumantai-gg/mithril-calibration/tree/main/aggregated`

- [ ] **Step 1: Fetch three aggregated files**
  ```bash
  for f in samwise.json arwen.json smaug.json; do
    curl -fsSL "https://raw.githubusercontent.com/moumantai-gg/mithril-calibration/main/aggregated/$f" -o "/tmp/mithril-cal-$f"
  done
  ```
  *Note: these aren't the calibration records this spec talks about — they're the per-module community-aggregated files. The actual calibration records this spec references are likely in a different file pattern.*

- [ ] **Step 2: Locate the calibration-records file pattern**
  ```bash
  curl -fsSL "https://api.github.com/repos/moumantai-gg/mithril-calibration/contents/aggregated" | jq -r '.[].name'
  ```
  Find the file(s) holding per-area `AreaCalibration`-shaped JSON (if any exist there at all — the repo may not ship them yet).

- [ ] **Step 3: Inspect `Source` distribution**
  For each calibration-records file found, count distinct `"source"` values:
  ```bash
  jq -r '[.. | objects | select(has("source")) | .source] | group_by(.) | map({src: .[0], count: length})' "/tmp/mithril-cal-records.json"
  ```
  Expected: `AutoCapture` only (per spec §7.2).

- [ ] **Step 4: Record outcome**
  If `AutoCapture` only → tick the verification box. If other sources present → STOP. The §7.2 `CommunitySync → Texture` inference needs revisiting.

- [ ] **Step 5: If the repo ships no calibration records yet**
  Tick the verification box with the note "no records exist as of YYYY-MM-DD — inference is forward-looking, validate again when records first land." Continue with PR 1.

### Task P.3: Legolas test-fixture audit for implicit-frame constants

**Files:**
- Read-only: 5+ files from `tests/Legolas.Tests/` that hard-code `PixelPoint` literals.

- [ ] **Step 1: Find candidate files**
  ```bash
  rg -l "new PixelPoint\(" tests/Legolas.Tests/
  ```

- [ ] **Step 2: Read 5 candidates** focusing on `MapOverlayViewModelTests`, `CoordinateProjectorTests`, `AdaptiveRouteOptimizerTests`, `AreaCalibrationServiceTests`, `MotherlodeReferenceLocatorTests`.

- [ ] **Step 3: For each, determine the implicit frame**
  Per file, record: "PixelPoint constants in this file mean: `<frame>`." Frame must be recoverable from surrounding context (variable names, call sites, comments). If not recoverable, mark "ambiguous" and surface in PR 5 planning.

- [ ] **Step 4: Record outcome**
  Tick the verification box. Attach a short table to `spec.md` §11's risk entry: file → frame.

### Task P.4: `MapRect` construction-site bucketing

**Files:**
- Read-only: every `new MapRect(` site.

- [ ] **Step 1: Find all construction sites**
  ```bash
  rg -n "new MapRect\(" src/ tests/
  ```

- [ ] **Step 2: Classify each site**
  For each site, record one of:
  - **bare `MapRect`** — origin is `(0, 0)` by construction (crop-aligned case);
  - **needs `LocatedMapRect`** — origin is in captured-frame coords (located-rect case);
  - **ambiguous / both** — would need a `MapRect` further-split (signals spec change required).

- [ ] **Step 3: Record outcome in spec**
  Tick the verification box. Attach the per-site classification table to `spec.md` §13 so PR 1 task 1.11/1.12 can reference it concretely.

- [ ] **Step 4: STOP if any "ambiguous / both" sites exist**
  Bring the finding back to the brainstorming flow before proceeding to PR 1. The §5.1 type restriction depends on this audit coming back clean.

---

## Phase 1 — New types alongside (PR 1)

**Branch:** `pixel-frame-typing-pr1-new-types`

**Acceptance:** Six new pixel structs, the `IPixelPoint` interface, two new calibration structs with a shared private math core, the `MapRect` typed conversions, `LocatedMapRect`, `MapCaptureRect.GameWindowToCaptured`, and `CanvasOverlayMapping` all land alongside the existing `PixelPoint` / `AreaCalibration`. **No consumer of the old types changes in this PR.** Full test suite stays green; new types have their own focused tests.

### Task 1.0: Prepare branch + verify clean baseline

- [ ] **Step 1: Confirm Mithril shell is not running**
  ```bash
  tasklist | grep -i Mithril
  ```
  Expected: no `Mithril.Shell.exe`. If present, close it (the pre-commit hook will block builds otherwise — see `mithril_build_file_lock_silent` memory note).

- [ ] **Step 2: Create branch from main**
  ```bash
  git checkout main && git pull && git checkout -b pixel-frame-typing-pr1-new-types
  ```

- [ ] **Step 3: Baseline build + test**
  ```bash
  dotnet build Mithril.slnx
  dotnet test Mithril.slnx --nologo
  ```
  Expected: green. If red on `main`, STOP and report.

### Task 1.1: `IPixelPoint` interface

**Files:**
- Create: `src/Mithril.MapCalibration/IPixelPoint.cs`
- Test: `tests/Mithril.MapCalibration.Tests/IPixelPointContractTests.cs` (will be created in Task 1.2 when there's a concrete struct to test it against; this task is interface-only)

- [ ] **Step 1: Create the interface**

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

- [ ] **Step 2: Build**
  ```bash
  dotnet build src/Mithril.MapCalibration/Mithril.MapCalibration.csproj
  ```
  Expected: green.

- [ ] **Step 3: Commit**
  ```bash
  git add src/Mithril.MapCalibration/IPixelPoint.cs
  git commit -m "feat(map-calibration): add IPixelPoint frame-erased interface (#1076 prep)"
  ```

### Task 1.2: `TexturePixel` — first concrete frame struct (also establishes the test pattern)

**Files:**
- Create: `src/Mithril.MapCalibration/TexturePixel.cs`
- Create: `tests/Mithril.MapCalibration.Tests/Frames/TexturePixelTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests.Frames;

public class TexturePixelTests
{
    [Fact]
    public void TwoArgCtor_DefaultsZToZero()
    {
        var p = new TexturePixel(3, 4);

        p.X.Should().Be(3);
        p.Y.Should().Be(4);
        p.Z.Should().Be(0);
    }

    [Fact]
    public void ThreeArgCtor_KeepsAllComponents()
    {
        var p = new TexturePixel(3, 4, 5);

        p.X.Should().Be(3);
        p.Y.Should().Be(4);
        p.Z.Should().Be(5);
    }

    [Fact]
    public void Zero_IsOrigin()
    {
        TexturePixel.Zero.Should().Be(new TexturePixel(0, 0, 0));
    }

    [Fact]
    public void DistanceTo_Uses2DMath_IgnoringZ()
    {
        var a = new TexturePixel(0, 0, 100);
        var b = new TexturePixel(3, 4, -100);

        a.DistanceTo(b).Should().Be(5);  // sqrt(9 + 16); Z ignored
    }

    [Fact]
    public void DistanceSquaredTo_Uses2DMath_IgnoringZ()
    {
        var a = new TexturePixel(0, 0, 100);
        var b = new TexturePixel(3, 4, -100);

        a.DistanceSquaredTo(b).Should().Be(25);
    }

    [Fact]
    public void EqualsByComponents()
    {
        var a = new TexturePixel(1, 2, 3);
        var b = new TexturePixel(1, 2, 3);
        var c = new TexturePixel(1, 2, 99);

        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    [Fact]
    public void ImplementsIPixelPoint()
    {
        IPixelPoint p = new TexturePixel(1, 2, 3);

        p.X.Should().Be(1);
        p.Y.Should().Be(2);
        p.Z.Should().Be(3);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~TexturePixelTests"
```
Expected: FAIL — "type or namespace TexturePixel could not be found."

- [ ] **Step 3: Implement `TexturePixel`**

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

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~TexturePixelTests"
```
Expected: PASS — 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/TexturePixel.cs tests/Mithril.MapCalibration.Tests/Frames/TexturePixelTests.cs
git commit -m "feat(map-calibration): add TexturePixel frame struct (#1076 prep)"
```

### Tasks 1.3 – 1.7: Other five pixel frame structs

Each frame is structurally identical to `TexturePixel`. Repeat the Task 1.2 pattern (failing test → run → implement → run → commit) per frame:

| Task | Struct | File | Doc line (replaces the `<see cref="WorldCoord"/>` paragraph) | Test file |
|---|---|---|---|---|
| **1.3** | `CapturedFramePixel` | `src/Mithril.MapCalibration/CapturedFramePixel.cs` | "A point in the full OS-captured-frame pixel space: origin at the captured frame's top-left, X right, Y down. Source for `MapRect.OriginX/OriginY` when describing a located rect, and for `LocateMetrics.Tx/Ty` from refiner outputs." | `tests/Mithril.MapCalibration.Tests/Frames/CapturedFramePixelTests.cs` |
| **1.4** | `CroppedFramePixel` | `src/Mithril.MapCalibration/CroppedFramePixel.cs` | "A point in the cropped-frame pixel space the detector consumed: origin at the located map rect's top-left within the captured frame, X right, Y down. Source for `TypedDetection.AnchorX/AnchorY`." | `tests/Mithril.MapCalibration.Tests/Frames/CroppedFramePixelTests.cs` |
| **1.5** | `OverlayPixel` | `src/Mithril.MapCalibration/OverlayPixel.cs` | "A point in the Mithril overlay window's pixel space: origin at the overlay window's top-left, X right, Y down. Source for all Legolas overlay rendering and `IWorldOverlayMarkers` outputs." | `tests/Mithril.MapCalibration.Tests/Frames/OverlayPixelTests.cs` |
| **1.6** | `CanvasPixel` | `src/Mithril.MapCalibration/CanvasPixel.cs` | "A point in WPF Canvas pixel space (mouse-event coordinates): origin at the canvas top-left, X right, Y down. Convert via `CanvasOverlayMapping` before crossing into overlay-frame code." | `tests/Mithril.MapCalibration.Tests/Frames/CanvasPixelTests.cs` |
| **1.7** | `GameWindowPixel` | `src/Mithril.MapCalibration/GameWindowPixel.cs` | "A point in the PG game-window client-area pixel space: origin at the DWM client-area top-left. Convert via `MapCaptureRect` before crossing into captured-frame code." | `tests/Mithril.MapCalibration.Tests/Frames/GameWindowPixelTests.cs` |

For each task: copy the Task 1.2 test file verbatim, replace `TexturePixel` with the target struct name and update the namespace declaration. Then copy the Task 1.2 struct file, replace `TexturePixel` with the target struct name and update the XML doc to the row's "Doc line." Commit each task separately:

```bash
git add src/Mithril.MapCalibration/<Frame>Pixel.cs tests/Mithril.MapCalibration.Tests/Frames/<Frame>PixelTests.cs
git commit -m "feat(map-calibration): add <Frame>Pixel frame struct (#1076 prep)"
```

### Task 1.8: `AreaProjectionCore` + `WorldToTextureCalibration`

**Files:**
- Create: `src/Mithril.MapCalibration/Internal/AreaProjectionCore.cs`
- Create: `src/Mithril.MapCalibration/WorldToTextureCalibration.cs`
- Create: `tests/Mithril.MapCalibration.Tests/WorldToTextureCalibrationTests.cs`
- Read-only reference: `src/Mithril.MapCalibration/AreaCalibration.cs:86-138`

- [ ] **Step 1: Write the failing equivalence test**

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class WorldToTextureCalibrationTests
{
    // Canonical fixture: identity scale, 30° rotation, mirror off, calibration zoom 1.
    private static readonly WorldToTextureCalibration Canonical = new(
        OriginX: 100,
        OriginY: 200,
        Scale: 4.0,
        RotationRadians: Math.PI / 6,
        MirrorNorth: false,
        CalibrationZoom: 1.0);

    // Same parameters expressed as the legacy AreaCalibration shape.
    private static readonly AreaCalibration LegacyEquivalent = new(
        OriginX: 100,
        OriginY: 200,
        Scale: 4.0,
        RotationRadians: Math.PI / 6,
        MirrorNorth: false,
        CalibrationZoom: 1.0);

    public static IEnumerable<object[]> Worlds() => new[]
    {
        new object[] { new WorldCoord(0, 0, 0) },
        new object[] { new WorldCoord(10, 0, 5) },
        new object[] { new WorldCoord(-15, 99, -3) }, // negative + non-zero Y
        new object[] { new WorldCoord(0, 0, 1000) },
    };

    [Theory, MemberData(nameof(Worlds))]
    public void ToTexture_MatchesLegacyWorldToWindow_BitIdentical(WorldCoord world)
    {
        var newResult = Canonical.ToTexture(world, currentZoom: 1.0);
        var oldResult = LegacyEquivalent.WorldToWindow(world, currentZoom: 1.0);

        newResult.X.Should().Be(oldResult.X);
        newResult.Y.Should().Be(oldResult.Y);
        newResult.Z.Should().Be(0); // texture frame Z always 0
    }

    [Theory, MemberData(nameof(Worlds))]
    public void FromTexture_MatchesLegacyWindowToWorld_BitIdentical(WorldCoord world)
    {
        // Round-trip through the new struct.
        var pixel = Canonical.ToTexture(world, 1.0);
        var newRoundTrip = Canonical.FromTexture(pixel, 1.0);

        // Round-trip through the old struct.
        var oldPixel = LegacyEquivalent.WorldToWindow(world, 1.0);
        var oldRoundTrip = LegacyEquivalent.WindowToWorld(oldPixel, 1.0);

        newRoundTrip.Should().NotBeNull();
        oldRoundTrip.Should().NotBeNull();
        newRoundTrip!.Value.X.Should().Be(oldRoundTrip!.Value.X);
        newRoundTrip.Value.Z.Should().Be(oldRoundTrip.Value.Z);
        // Y (elevation) is dropped by both; both return 0.
    }

    [Fact]
    public void ToTexture_HonoursZoomFactor()
    {
        var atUnitZoom = Canonical.ToTexture(new WorldCoord(10, 0, 0), 1.0);
        var atDoubleZoom = Canonical.ToTexture(new WorldCoord(10, 0, 0), 2.0);

        // Doubling currentZoom doubles the effective scale → pixel offset from origin doubles.
        var unitOffsetX = atUnitZoom.X - Canonical.OriginX;
        var doubleOffsetX = atDoubleZoom.X - Canonical.OriginX;
        doubleOffsetX.Should().BeApproximately(2 * unitOffsetX, 1e-9);
    }

    [Fact]
    public void MirrorNorth_FlipsZAxis()
    {
        var unmirrored = Canonical with { MirrorNorth = false };
        var mirrored = Canonical with { MirrorNorth = true };

        var world = new WorldCoord(0, 0, 10);
        var u = unmirrored.ToTexture(world, 1.0);
        var m = mirrored.ToTexture(world, 1.0);

        // The mirror flips the north component → the Y offset from origin inverts.
        var uOffsetY = u.Y - Canonical.OriginY;
        var mOffsetY = m.Y - Canonical.OriginY;
        mOffsetY.Should().BeApproximately(-uOffsetY, 1e-9);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~WorldToTextureCalibrationTests"
```
Expected: FAIL — type not defined.

- [ ] **Step 3: Implement `AreaProjectionCore`**

Read `src/Mithril.MapCalibration/AreaCalibration.cs` lines 86-138 first to see the canonical math. Then create the core:

```csharp
namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Frame-agnostic world ↔ pixel projection arithmetic shared by
/// <see cref="WorldToTextureCalibration"/> and <see cref="WorldToOverlayCalibration"/>.
/// Pulled out so both wrappers share one source of truth for the rotation +
/// scale + mirror + zoom math; the only difference between the two wrappers
/// is the return type tagging.
///
/// Math is bit-identical to the legacy AreaCalibration.WorldToWindow /
/// WindowToWorld (pre-#1076 refactor); see WorldToTextureCalibrationTests for
/// the equivalence assertions.
/// </summary>
internal static class AreaProjectionCore
{
    public static (double X, double Y) Project(
        double originX, double originY, double scale, double rotationRadians,
        bool mirrorNorth, double calibrationZoom,
        WorldCoord world, double currentZoom)
    {
        var effScale = scale * ZoomFactor(currentZoom, calibrationZoom);
        var east = world.X;
        var north = mirrorNorth ? -world.Z : world.Z;
        var cos = Math.Cos(rotationRadians);
        var sin = Math.Sin(rotationRadians);
        var rotE = east * cos + north * sin;
        var rotN = -east * sin + north * cos;
        return (originX + effScale * rotE, originY - effScale * rotN);
    }

    public static WorldCoord? Unproject(
        double originX, double originY, double scale, double rotationRadians,
        bool mirrorNorth, double calibrationZoom,
        double pixelX, double pixelY, double currentZoom)
    {
        var effScale = scale * ZoomFactor(currentZoom, calibrationZoom);
        if (effScale <= 1e-9) return null;

        var rotE = (pixelX - originX) / effScale;
        var rotN = -(pixelY - originY) / effScale;

        var cos = Math.Cos(rotationRadians);
        var sin = Math.Sin(rotationRadians);
        var east = rotE * cos - rotN * sin;
        var north = rotE * sin + rotN * cos;

        var worldX = east;
        var worldZ = mirrorNorth ? -north : north;
        return new WorldCoord(worldX, 0, worldZ);
    }

    private static double ZoomFactor(double currentZoom, double calibrationZoom) =>
        (currentZoom > 1e-6 && calibrationZoom > 1e-6)
            ? currentZoom / calibrationZoom
            : 1.0;
}
```

- [ ] **Step 4: Implement `WorldToTextureCalibration`**

```csharp
using Mithril.MapCalibration.Internal;

namespace Mithril.MapCalibration;

/// <summary>
/// World → base-texture-pixel projection. Owned by Capture/Detection — this is
/// the calibration shape produced by the AutoCalibration RANSAC solve and read
/// by the drift-check.
///
/// Sibling of <see cref="WorldToOverlayCalibration"/>; the two structs hold
/// the same math (delegated to <see cref="AreaProjectionCore"/>) but return
/// frame-typed pixel results so a texture-frame calibration cannot be
/// silently fed to overlay-frame code or vice versa.
/// </summary>
public readonly record struct WorldToTextureCalibration(
    double OriginX,
    double OriginY,
    double Scale,
    double RotationRadians,
    bool MirrorNorth,
    double CalibrationZoom)
{
    public int SchemaVersion { get; init; } = 1;

    public TexturePixel ToTexture(WorldCoord world, double currentZoom)
    {
        var (x, y) = AreaProjectionCore.Project(
            OriginX, OriginY, Scale, RotationRadians, MirrorNorth,
            CalibrationZoom, world, currentZoom);
        return new TexturePixel(x, y);
    }

    public TexturePixel ToTexture(WorldCoord world) => ToTexture(world, CalibrationZoom);

    public WorldCoord? FromTexture(TexturePixel pixel, double currentZoom) =>
        AreaProjectionCore.Unproject(
            OriginX, OriginY, Scale, RotationRadians, MirrorNorth,
            CalibrationZoom, pixel.X, pixel.Y, currentZoom);

    public WorldCoord? FromTexture(TexturePixel pixel) => FromTexture(pixel, CalibrationZoom);
}
```

- [ ] **Step 5: Run test to verify pass**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~WorldToTextureCalibrationTests"
```
Expected: PASS (6 tests, 4 from `Worlds` × 1 fact + 2 standalone facts).

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration/Internal/AreaProjectionCore.cs \
        src/Mithril.MapCalibration/WorldToTextureCalibration.cs \
        tests/Mithril.MapCalibration.Tests/WorldToTextureCalibrationTests.cs
git commit -m "feat(map-calibration): add WorldToTextureCalibration + shared AreaProjectionCore (#1076 prep)"
```

### Task 1.9: `WorldToOverlayCalibration`

**Files:**
- Create: `src/Mithril.MapCalibration/WorldToOverlayCalibration.cs`
- Create: `tests/Mithril.MapCalibration.Tests/WorldToOverlayCalibrationTests.cs`

- [ ] **Step 1: Write the failing test**
  Copy `WorldToTextureCalibrationTests.cs` verbatim; rename: `WorldToTextureCalibration` → `WorldToOverlayCalibration`; `ToTexture` → `ToOverlay`; `FromTexture` → `FromOverlay`; `TexturePixel` → `OverlayPixel`. Same fixture, same legacy-equivalent assertions.

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~WorldToOverlayCalibrationTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement `WorldToOverlayCalibration`**

Copy `WorldToTextureCalibration.cs` verbatim; rename: `WorldToTextureCalibration` → `WorldToOverlayCalibration`; `ToTexture` → `ToOverlay`; `FromTexture` → `FromOverlay`; `TexturePixel` → `OverlayPixel`. Update the XML doc to say "World → overlay-pixel projection. Owned by Mithril.Overlay / Legolas."

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~WorldToOverlayCalibrationTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/WorldToOverlayCalibration.cs \
        tests/Mithril.MapCalibration.Tests/WorldToOverlayCalibrationTests.cs
git commit -m "feat(map-calibration): add WorldToOverlayCalibration (#1076 prep)"
```

### Task 1.10: `ProjectThroughOverlay` bridge

**Files:**
- Modify: `src/Mithril.MapCalibration/WorldToTextureCalibration.cs`
- Modify: `tests/Mithril.MapCalibration.Tests/WorldToTextureCalibrationTests.cs`

This task depends on `MapRect` typed conversions (Task 1.11) — defer until Task 1.11 is complete. Place this task immediately after Task 1.11 instead.

(See the **revised ordering** at Task 1.11's end.)

### Task 1.11: `MapRect` typed conversions

**Files:**
- Modify: `src/Mithril.MapCalibration/MapRect.cs`
- Create: `tests/Mithril.MapCalibration.Tests/MapRectTypedConversionsTests.cs`

- [ ] **Step 1: Read existing `MapRect.cs`**
  Find the existing `ScreenshotToTexture(double, double)` method. The new typed methods sit alongside it; the legacy double-arg method stays untouched in PR 1.

- [ ] **Step 2: Write the failing test**

```csharp
using FluentAssertions;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class MapRectTypedConversionsTests
{
    // A MapRect describing a 200×100 crop carved from a 1000×500 base texture,
    // with the crop's region-of-interest starting at texture-pixel (50, 25).
    private static readonly MapRect CropAligned = new(
        OriginX: 0, OriginY: 0,       // crop-aligned: origin in screenshot-own-frame is (0,0)
        Width: 200, Height: 100,
        TextureWidth: 1000, TextureHeight: 500);

    [Fact]
    public void CroppedToTexture_ScalesUpByAspectRatio()
    {
        var cropPixel = new CroppedFramePixel(100, 50); // center of the crop
        var texPixel = CropAligned.CroppedToTexture(cropPixel);

        // Crop is 200×100 mapped onto 1000×500 base — 5× scale on both axes.
        texPixel.X.Should().BeApproximately(500, 1e-9);
        texPixel.Y.Should().BeApproximately(250, 1e-9);
    }

    [Fact]
    public void TextureToCropped_RoundTripsCroppedToTexture()
    {
        var original = new CroppedFramePixel(37, 13);
        var roundTrip = CropAligned.TextureToCropped(CropAligned.CroppedToTexture(original));

        roundTrip.X.Should().BeApproximately(original.X, 1e-9);
        roundTrip.Y.Should().BeApproximately(original.Y, 1e-9);
    }

    [Fact]
    public void CroppedToTexture_DoesNotRequireZ()
    {
        // CroppedFramePixel(X, Y) defaults Z to 0; texture frame also Z=0.
        var texPixel = CropAligned.CroppedToTexture(new CroppedFramePixel(0, 0));
        texPixel.Z.Should().Be(0);
    }
}
```

- [ ] **Step 3: Run test to verify failure**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapRectTypedConversionsTests"
```
Expected: FAIL — methods not defined.

- [ ] **Step 4: Add typed conversions to `MapRect`**

Append these methods to the existing `MapRect` struct (do NOT modify the legacy `ScreenshotToTexture` — leave it for PR 2 to retire):

```csharp
/// <summary>
/// Typed projection from a cropped-frame pixel (the screenshot the detector
/// consumed) into the base-texture frame. Replaces the legacy
/// double-based <see cref="ScreenshotToTexture"/> for crop-aligned cases
/// (where the screenshot equals the crop and origin is (0,0)).
///
/// Only valid for crop-aligned <see cref="MapRect"/> instances — see §5.1
/// of the pixel-frame-typing spec. For located-rect cases use
/// <see cref="LocatedMapRect"/>.
/// </summary>
public TexturePixel CroppedToTexture(CroppedFramePixel pixel)
{
    var sx = TextureWidth / (double)Width;
    var sy = TextureHeight / (double)Height;
    return new TexturePixel((pixel.X - OriginX) * sx, (pixel.Y - OriginY) * sy);
}

/// <summary>Inverse of <see cref="CroppedToTexture"/>.</summary>
public CroppedFramePixel TextureToCropped(TexturePixel pixel)
{
    var sx = Width / (double)TextureWidth;
    var sy = Height / (double)TextureHeight;
    return new CroppedFramePixel(pixel.X * sx + OriginX, pixel.Y * sy + OriginY);
}
```

- [ ] **Step 5: Run test to verify pass**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapRectTypedConversionsTests"
```
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration/MapRect.cs \
        tests/Mithril.MapCalibration.Tests/MapRectTypedConversionsTests.cs
git commit -m "feat(map-calibration): add MapRect typed Cropped↔Texture conversions (#1076 prep)"
```

### Task 1.10 (revised): `ProjectThroughOverlay` on `WorldToTextureCalibration`

Now that `MapRect` typed conversions exist (Task 1.11), wire the texture↔overlay bridge.

**Files:**
- Modify: `src/Mithril.MapCalibration/WorldToTextureCalibration.cs`
- Modify: `tests/Mithril.MapCalibration.Tests/WorldToTextureCalibrationTests.cs`

- [ ] **Step 1: Write the failing test (append to existing file)**

```csharp
[Fact]
public void ProjectThroughOverlay_ComposesTextureFrameOntoOverlayRect()
{
    // A texture-frame calibration with known parameters.
    var texCal = new WorldToTextureCalibration(
        OriginX: 100, OriginY: 200, Scale: 4.0,
        RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0);

    // The texture renders onto the overlay at a known placement:
    // the overlay shows the 1000×500 texture at half-size starting at overlay (30, 40).
    var overlayRect = new MapRect(
        OriginX: 30, OriginY: 40,
        Width: 500, Height: 250,
        TextureWidth: 1000, TextureHeight: 500);

    var overlayCal = texCal.ProjectThroughOverlay(overlayRect);

    // A world point projected through texCal then composed onto the overlay
    // should equal projecting through the resulting overlayCal directly.
    var world = new WorldCoord(7, 0, 3);
    var viaCompose = texCal.ToTexture(world);
    var expectedOverlay = new OverlayPixel(
        overlayRect.OriginX + (viaCompose.X * overlayRect.Width / overlayRect.TextureWidth),
        overlayRect.OriginY + (viaCompose.Y * overlayRect.Height / overlayRect.TextureHeight));

    var viaBridge = overlayCal.ToOverlay(world);

    viaBridge.X.Should().BeApproximately(expectedOverlay.X, 1e-9);
    viaBridge.Y.Should().BeApproximately(expectedOverlay.Y, 1e-9);
}
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~WorldToTextureCalibrationTests.ProjectThroughOverlay"
```
Expected: FAIL — method not defined.

- [ ] **Step 3: Implement `ProjectThroughOverlay`**

Append to `WorldToTextureCalibration`:

```csharp
/// <summary>
/// Compose this texture-frame calibration with a base-texture placement on
/// the overlay window, yielding the equivalent overlay-frame calibration.
/// This is the ONE named place where texture-frame and overlay-frame
/// calibrations talk to each other (spec §6.2); rendering an
/// AutoCalibration-derived calibration onto the overlay goes through here.
/// </summary>
public WorldToOverlayCalibration ProjectThroughOverlay(MapRect overlayRect)
{
    var sx = overlayRect.Width / (double)overlayRect.TextureWidth;
    var sy = overlayRect.Height / (double)overlayRect.TextureHeight;
    return new WorldToOverlayCalibration(
        OriginX: overlayRect.OriginX + OriginX * sx,
        OriginY: overlayRect.OriginY + OriginY * sy,
        // The composed scale uses sx — overlay-frame X and Y scale identically in
        // the canonical case. If sx != sy ever becomes a real consumer need, the
        // texture↔overlay placement is anisotropic and this composition is wrong;
        // fail loudly there instead of silently picking one axis.
        Scale: Scale * sx,
        RotationRadians: RotationRadians,
        MirrorNorth: MirrorNorth,
        CalibrationZoom: CalibrationZoom);
}
```

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~WorldToTextureCalibrationTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/WorldToTextureCalibration.cs \
        tests/Mithril.MapCalibration.Tests/WorldToTextureCalibrationTests.cs
git commit -m "feat(map-calibration): add ProjectThroughOverlay texture→overlay bridge (#1076 prep)"
```

### Task 1.12: `LocatedMapRect`

**Files:**
- Create: `src/Mithril.MapCalibration/LocatedMapRect.cs`
- Create: `tests/Mithril.MapCalibration.Tests/LocatedMapRectTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class LocatedMapRectTests
{
    private static readonly MapRect Inner = new(
        OriginX: 0, OriginY: 0,
        Width: 200, Height: 100,
        TextureWidth: 1000, TextureHeight: 500);

    private static readonly CapturedFramePixel CapturedOrigin = new(320, 58);

    private static readonly LocatedMapRect Located = new(Inner, CapturedOrigin);

    [Fact]
    public void CroppedToCaptured_AddsOrigin()
    {
        var crop = new CroppedFramePixel(10, 20);
        var captured = Located.CroppedToCaptured(crop);

        captured.X.Should().Be(330);
        captured.Y.Should().Be(78);
    }

    [Fact]
    public void CapturedToCropped_SubtractsOrigin()
    {
        var captured = new CapturedFramePixel(330, 78);
        var crop = Located.CapturedToCropped(captured);

        crop.X.Should().Be(10);
        crop.Y.Should().Be(20);
    }

    [Fact]
    public void RoundTrip_CroppedThroughCapturedAndBack()
    {
        var original = new CroppedFramePixel(37, 13);
        var roundTrip = Located.CapturedToCropped(Located.CroppedToCaptured(original));

        roundTrip.X.Should().BeApproximately(original.X, 1e-9);
        roundTrip.Y.Should().BeApproximately(original.Y, 1e-9);
    }

    [Fact]
    public void MapRect_ExposesInnerRectForTextureSideConversions()
    {
        Located.MapRect.Should().Be(Inner);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~LocatedMapRectTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement `LocatedMapRect`**

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// A <see cref="MapRect"/> together with its origin in captured-frame
/// coordinates — i.e. where the located map rect sits within the full OS
/// capture. Use for the "located rect" case in the auto-calibration pipeline;
/// use bare <see cref="MapRect"/> for crop-aligned cases.
///
/// See spec §5.1 for why this split exists: bare <see cref="MapRect"/>
/// describes texture↔crop with the crop's origin pinned at (0,0) in its own
/// frame; <see cref="LocatedMapRect"/> additionally carries the crop's
/// placement within the captured frame.
/// </summary>
public readonly record struct LocatedMapRect(MapRect MapRect, CapturedFramePixel Origin)
{
    public CapturedFramePixel CroppedToCaptured(CroppedFramePixel pixel) =>
        new(pixel.X + Origin.X, pixel.Y + Origin.Y);

    public CroppedFramePixel CapturedToCropped(CapturedFramePixel pixel) =>
        new(pixel.X - Origin.X, pixel.Y - Origin.Y);
}
```

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~LocatedMapRectTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/LocatedMapRect.cs \
        tests/Mithril.MapCalibration.Tests/LocatedMapRectTests.cs
git commit -m "feat(map-calibration): add LocatedMapRect for crop-in-captured-frame placement (#1076 prep)"
```

### Task 1.13: `MapCaptureRect.GameWindowToCaptured`

**Files:**
- Read-only: `src/Mithril.MapCalibration/CaptureRectMath.cs` (existing math)
- Modify: `src/Mithril.MapCalibration.Capture/MapCaptureRect.cs` (or wherever `MapCaptureRect` lives — find via `rg`)
- Create: typed-conversion tests in the appropriate test project

- [ ] **Step 1: Locate `MapCaptureRect`**

```bash
rg -l "MapCaptureRect" src/
```
Expected: file path. Read it to understand the existing fields (likely `OriginX/Y, Width, Height` in game-window coords, or `IMapCaptureRectStore` over `ShellSettings`).

- [ ] **Step 2: Write the failing test**
  Create `tests/<appropriate-project>/MapCaptureRectTypedConversionsTests.cs` with:

```csharp
[Fact]
public void GameWindowToCaptured_TranslatesByRectOrigin()
{
    var rect = new MapCaptureRect(/* OriginX: */ 100, /* OriginY: */ 50,
                                  /* Width: */ 800, /* Height: */ 600);

    var captured = rect.GameWindowToCaptured(new GameWindowPixel(110, 60));
    captured.X.Should().Be(10);
    captured.Y.Should().Be(10);
}

[Fact]
public void CapturedToGameWindow_AddsRectOrigin()
{
    var rect = new MapCaptureRect(100, 50, 800, 600);
    var gw = rect.CapturedToGameWindow(new CapturedFramePixel(10, 10));
    gw.X.Should().Be(110);
    gw.Y.Should().Be(60);
}
```
*Adjust field names to whatever `MapCaptureRect` actually carries.*

- [ ] **Step 3: Run test to verify failure**

- [ ] **Step 4: Add the typed methods to `MapCaptureRect`**

```csharp
public CapturedFramePixel GameWindowToCaptured(GameWindowPixel pixel) =>
    new(pixel.X - /* rect origin X */, pixel.Y - /* rect origin Y */);

public GameWindowPixel CapturedToGameWindow(CapturedFramePixel pixel) =>
    new(pixel.X + /* rect origin X */, pixel.Y + /* rect origin Y */);
```

- [ ] **Step 5: Run test to verify pass**

- [ ] **Step 6: Commit**

```bash
git add src/<path>/MapCaptureRect.cs tests/<path>/MapCaptureRectTypedConversionsTests.cs
git commit -m "feat(map-calibration): add MapCaptureRect typed GameWindow↔Captured conversions (#1076 prep)"
```

### Task 1.14: `CanvasOverlayMapping`

**Files:**
- Create: `src/Mithril.Overlay/CanvasOverlayMapping.cs`
- Create: `tests/Mithril.Overlay.Tests/CanvasOverlayMappingTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.Overlay.Tests;

public class CanvasOverlayMappingTests
{
    [Fact]
    public void IdentityDpi_IsIdentity()
    {
        var m = new CanvasOverlayMapping(dpiScale: 1.0);

        var canvas = new CanvasPixel(100, 200);
        var overlay = m.CanvasToOverlay(canvas);

        overlay.X.Should().Be(100);
        overlay.Y.Should().Be(200);
    }

    [Fact]
    public void NonIdentityDpi_ScalesBothAxes()
    {
        var m = new CanvasOverlayMapping(dpiScale: 1.5);

        var canvas = new CanvasPixel(100, 200);
        var overlay = m.CanvasToOverlay(canvas);

        overlay.X.Should().Be(150);
        overlay.Y.Should().Be(300);
    }

    [Fact]
    public void OverlayToCanvas_InvertsCanvasToOverlay()
    {
        var m = new CanvasOverlayMapping(dpiScale: 1.5);

        var original = new CanvasPixel(37, 13);
        var roundTrip = m.OverlayToCanvas(m.CanvasToOverlay(original));

        roundTrip.X.Should().BeApproximately(original.X, 1e-9);
        roundTrip.Y.Should().BeApproximately(original.Y, 1e-9);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test tests/Mithril.Overlay.Tests --filter "FullyQualifiedName~CanvasOverlayMappingTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
namespace Mithril.Overlay;

using Mithril.MapCalibration;

/// <summary>
/// Conversion between a WPF Canvas's pixel space (mouse-event coordinates)
/// and the Mithril overlay window's pixel space. Today this is identity at
/// DPI=1 — when per-monitor DPI scaling lands, this is the one type that
/// needs to learn about real DPI math; the type system catches every site
/// that needs to update.
/// </summary>
public readonly record struct CanvasOverlayMapping(double DpiScale)
{
    public OverlayPixel CanvasToOverlay(CanvasPixel pixel) =>
        new(pixel.X * DpiScale, pixel.Y * DpiScale);

    public CanvasPixel OverlayToCanvas(OverlayPixel pixel) =>
        new(pixel.X / DpiScale, pixel.Y / DpiScale);
}
```

- [ ] **Step 4: Run test to verify pass**

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Overlay/CanvasOverlayMapping.cs tests/Mithril.Overlay.Tests/CanvasOverlayMappingTests.cs
git commit -m "feat(overlay): add CanvasOverlayMapping DPI-aware Canvas↔Overlay conversion (#1076 prep)"
```

### Task 1.15: PR 1 full verification + open PR

- [ ] **Step 1: Full solution build**

```bash
dotnet build Mithril.slnx
```
Expected: green, no warnings beyond the existing baseline.

- [ ] **Step 2: Full solution test**

```bash
dotnet test Mithril.slnx --nologo
```
Expected: all existing tests still green + ~50 new tests from PR 1 tasks.

- [ ] **Step 3: Confirm zero changes to old types**

```bash
git diff main -- src/Mithril.MapCalibration/PixelPoint.cs \
                src/Mithril.MapCalibration/AreaCalibration.cs \
                src/Mithril.MapCalibration/IMapCalibrationService.cs \
                src/Mithril.MapCalibration/MapRect.cs
```
Expected: `MapRect.cs` shows additive methods only (no signature changes); the other three show zero diff.

- [ ] **Step 4: Push branch**

```bash
git push -u origin pixel-frame-typing-pr1-new-types
```

- [ ] **Step 5: Open PR**

```bash
gh pr create --title "Pixel-frame typing PR 1/7: introduce new frame structs alongside" \
  --body "$(cat <<'EOF'
## Summary

PR 1 of 7 in the pixel-frame-typing refactor (spec: `docs/planning/calibration-1076-pixel-frame-typing/spec.md`). Introduces the new frame-tagged types alongside the existing `PixelPoint` / `AreaCalibration`; no existing consumer changes.

- 6 concrete pixel structs (`TexturePixel`, `CapturedFramePixel`, `CroppedFramePixel`, `OverlayPixel`, `CanvasPixel`, `GameWindowPixel`) implementing the new `IPixelPoint` interface.
- `WorldToTextureCalibration` + `WorldToOverlayCalibration` sharing one private `AreaProjectionCore` math core. Bit-identical to the legacy `AreaCalibration.WorldToWindow` (asserted by equivalence tests).
- `ProjectThroughOverlay` texture→overlay bridge.
- `MapRect` typed conversions (crop-aligned case) + new `LocatedMapRect` wrapper for captured-frame placement.
- `MapCaptureRect.GameWindowToCaptured/CapturedToGameWindow`.
- `CanvasOverlayMapping` (identity at DPI=1; placeholder for future per-monitor DPI).

Spec-mandated pre-flight verifications (P.1–P.4) closed before this PR opened; see commit history.

## Test plan
- [x] All existing tests still green
- [x] New types each have focused per-frame tests
- [x] `WorldToTextureCalibration` / `WorldToOverlayCalibration` math asserts bit-identical equivalence to legacy `AreaCalibration` on the same parameters

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Phase 2 — Migrate `Mithril.MapCalibration` core (PR 2)

**Branch:** `pixel-frame-typing-pr2-migrate-core`

**Depends on:** PR 1 merged to main.

**Acceptance:** Internals of `Mithril.MapCalibration` use the new types. `IMapCalibrationService` grows the new four methods and marks the old two `[Obsolete]` with delegating shims. Consumers outside this project don't change yet (they'll be migrated in PRs 3–5). Full test suite green.

### Migration pattern for Phase 2

This pattern repeats for each consumer file. When the file is mechanical, only the per-file delta needs to be spelled out — the pattern is constant.

Per file:
1. Replace `PixelPoint` parameter / return / field types with the appropriate `<Frame>Pixel`.
2. At call sites that produced or consumed the value, walk back/forward to find the originating frame (memory note: pixel frame is recoverable from variable name + call-site context per the P.3 audit).
3. Replace `new PixelPoint(x, y)` with `new <Frame>Pixel(x, y)`.
4. If the value crosses frames internally, route through the named conversion method on the value that owns the relationship (see spec §5 table).
5. Run targeted tests.
6. Commit per-file.

### Task 2.0: Branch + baseline

- [ ] Pre-commit: confirm Mithril shell not running.
- [ ] `git checkout main && git pull && git checkout -b pixel-frame-typing-pr2-migrate-core`
- [ ] `dotnet build Mithril.slnx && dotnet test Mithril.slnx --nologo` — expect green baseline.

### Task 2.1: `LandmarkCalibrationSolver.Reference`

**Files:**
- Modify: `src/Mithril.MapCalibration/LandmarkCalibrationSolver.cs`
- Modify: `tests/Mithril.MapCalibration.Tests/Internal/LandmarkCalibrationSolverTests.cs` (or wherever the solver tests live — find via `rg`)
- Modify: any consumer that constructs `LandmarkCalibrationSolver.Reference` (today: `TypeAwareRansacSolver`, deferred to PR 3 — for now, leave the field as-is)

The solver internally solves a similarity transform from `(WorldX, WorldZ) ↔ Pixel` pairs. The pixel side could be either texture or overlay depending on caller; the solver itself is frame-agnostic. Two paths:
- (a) **Generic the solver over the pixel frame**: `LandmarkCalibrationSolver<TPixel>` where `TPixel : IPixelPoint`. Forces every caller to declare the frame at the type level; loses the `IPixelPoint` constraint's benefits if we want to avoid generics in PR 1's design.
- (b) **Keep the solver double-based internally; let callers wrap inputs/outputs.** The solver stays as today; `Reference` becomes a `(double WorldX, double WorldZ, double PixelX, double PixelY)` record.

Choice for this plan: **(b) — solver stays double-based.** The frame discipline is the caller's responsibility; the solver doesn't claim to know it. This matches the existing pattern (`WorldX/WorldZ` are raw doubles too) and avoids generic propagation.

- [ ] **Step 1: Modify `Reference`**

```csharp
// before:
public readonly record struct Reference(double WorldX, double WorldZ, PixelPoint Pixel);

// after:
public readonly record struct Reference(double WorldX, double WorldZ, double PixelX, double PixelY);
```

- [ ] **Step 2: Update solver internals**
  Anywhere `.Pixel.X` / `.Pixel.Y` is read, change to `.PixelX` / `.PixelY`.

- [ ] **Step 3: Update tests in this project**
  Construction-site sweep: `new Reference(wX, wZ, new PixelPoint(px, py))` → `new Reference(wX, wZ, px, py)`.

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~LandmarkCalibrationSolver"
```
Expected: green.

- [ ] **Step 5: Build the whole solution** (other projects construct `Reference` too — PR 3's targets, but they need to compile)

```bash
dotnet build Mithril.slnx
```
Expected: red — `TypeAwareRansacSolver.cs` lines 142/143/211/270 use `new PixelPoint(...)` in `Reference` construction. **This is expected breakage; fix in step 6.**

- [ ] **Step 6: Quick mechanical fix in `TypeAwareRansacSolver.cs`**
  At each of the four construction sites, replace `new PixelPoint(x, y)` with the two raw double args. Do NOT alter the surrounding logic — that's PR 3's job. This is just keeping the build green.
  ```csharp
  // before:
  new LandmarkCalibrationSolver.Reference(r1.World.X, r1.World.Z, new PixelPoint(e1.Tx, e1.Ty)),
  // after:
  new LandmarkCalibrationSolver.Reference(r1.World.X, r1.World.Z, e1.Tx, e1.Ty),
  ```

- [ ] **Step 7: Re-build**
  ```bash
  dotnet build Mithril.slnx
  ```
  Expected: green.

- [ ] **Step 8: Commit**

```bash
git add src/Mithril.MapCalibration/LandmarkCalibrationSolver.cs \
        src/Mithril.MapCalibration.Detection/TypeAwareRansacSolver.cs \
        tests/Mithril.MapCalibration.Tests/Internal/LandmarkCalibrationSolverTests.cs
git commit -m "refactor(map-calibration): drop PixelPoint from LandmarkCalibrationSolver.Reference (#1076)"
```

### Task 2.2: `CandidateTransform.Apply`

**Files:**
- Modify: `src/Mithril.MapCalibration/CandidateTransform.cs`
- Modify: `tests/Mithril.MapCalibration.Tests/Internal/CandidateTransformTests.cs`

`CandidateTransform.Apply(WorldCoord) -> PixelPoint` describes a candidate (origin, scale, rotation, tx, ty) transform produced by the RANSAC solver. The output frame is "texture" (the RANSAC solver operates over texture pixels — see `TypeAwareRansacSolver`).

- [ ] **Step 1: Modify signature**
  ```csharp
  // before:
  public PixelPoint Apply(WorldCoord world) { ... return new PixelPoint(Tx + Scale * rotE, Ty - Scale * rotN); }
  // after:
  public TexturePixel Apply(WorldCoord world) { ... return new TexturePixel(Tx + Scale * rotE, Ty - Scale * rotN); }
  ```

- [ ] **Step 2: Update tests**
  Any `PixelPoint` assertion type becomes `TexturePixel`. Numeric expectations unchanged.

- [ ] **Step 3: Build** — expect breakage at consumers (`TypeAwareRansacSolver`, others). Fix mechanically: `PixelPoint result = candidate.Apply(...)` → `TexturePixel result = candidate.Apply(...)`.

- [ ] **Step 4: `dotnet test Mithril.slnx --filter "FullyQualifiedName~CandidateTransform"`** — green.

- [ ] **Step 5: Commit**
  ```bash
  git add src/Mithril.MapCalibration/CandidateTransform.cs \
          src/Mithril.MapCalibration.Detection/TypeAwareRansacSolver.cs \
          tests/Mithril.MapCalibration.Tests/Internal/CandidateTransformTests.cs
  git commit -m "refactor(map-calibration): CandidateTransform.Apply returns TexturePixel (#1076)"
  ```

### Task 2.3: Internal `MapCalibrationService` storage split

**Files:**
- Modify: `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`
- Modify: `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs`
- Modify: `src/Mithril.MapCalibration/CommunityCalibrationSync.cs` (or wherever community calibration loads)
- Modify: relevant tests

- [ ] **Step 1: Add the two typed dictionaries**

```csharp
private readonly Dictionary<MapSceneRef, IReadOnlyList<WorldToTextureCalibration>> _textureRecords;
private readonly Dictionary<MapSceneRef, IReadOnlyList<WorldToOverlayCalibration>> _overlayRecords;
```

- [ ] **Step 2: Add routing logic in the loader**
  For each loaded `AreaCalibration` record, determine the frame:
  - `Source: AutoCapture` → Texture.
  - `Source: BundledBaseline` → Texture.
  - `Source: CommunitySync` → Texture.
  - `Source: UserRefinement` → Texture **if** loaded from `UserRefinementStore`'s file (PR P.1 audit confirms this is exclusive); Overlay **if** loaded from `LegolasSettings.AreaCalibrations`. The loader knows its source file at this point.

  Wrap each `AreaCalibration` into the matching new struct:
  ```csharp
  WorldToTextureCalibration FromLegacyAsTexture(AreaCalibration legacy) =>
      new(legacy.OriginX, legacy.OriginY, legacy.Scale, legacy.RotationRadians,
          legacy.MirrorNorth, legacy.CalibrationZoom);

  WorldToOverlayCalibration FromLegacyAsOverlay(AreaCalibration legacy) =>
      new(legacy.OriginX, legacy.OriginY, legacy.Scale, legacy.RotationRadians,
          legacy.MirrorNorth, legacy.CalibrationZoom);
  ```

- [ ] **Step 3: Keep the legacy storage in parallel**
  Don't remove the existing legacy `Dictionary<MapSceneRef, IReadOnlyList<AreaCalibration>>` — the `[Obsolete]` shims in Task 2.5 read from it. PR 7 removes it.

- [ ] **Step 4: Update existing tests** to assert that records land in the right typed dictionary.

- [ ] **Step 5: `dotnet test Mithril.slnx --filter "FullyQualifiedName~MapCalibrationService"`** — green.

- [ ] **Step 6: Commit**
  ```bash
  git add src/Mithril.MapCalibration/Internal/MapCalibrationService.cs \
          src/Mithril.MapCalibration/Internal/UserRefinementStore.cs \
          tests/Mithril.MapCalibration.Tests/MapCalibrationServiceTests.cs \
          tests/Mithril.MapCalibration.Tests/MapCalibrationServicePickerTests.cs
  git commit -m "refactor(map-calibration): split internal storage by frame in MapCalibrationService (#1076)"
  ```

### Task 2.4: `IMapCalibrationService` new methods

**Files:**
- Modify: `src/Mithril.MapCalibration/IMapCalibrationService.cs`
- Modify: `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`
- Modify: relevant tests

- [ ] **Step 1: Write failing tests for the new methods**

```csharp
[Fact]
public void WorldToTexture_ReturnsNull_WhenSceneHasOnlyOverlayRecords()
{
    var svc = BuildServiceWith(scene, overlayRecords: new[] { someOverlayCal });
    svc.WorldToTexture(scene, new WorldCoord(0, 0, 0), currentZoom: 1.0)
       .Should().BeNull();
}

[Fact]
public void WorldToOverlay_ReturnsResult_FromOverlayRecord()
{
    var svc = BuildServiceWith(scene, overlayRecords: new[] { someOverlayCal });
    svc.WorldToOverlay(scene, new WorldCoord(0, 0, 0), currentZoom: 1.0)
       .Should().NotBeNull();
}

// + two more for TextureToWorld / OverlayToWorld
```

- [ ] **Step 2: Add new methods to interface**

```csharp
TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom);
WorldCoord? TextureToWorld(MapSceneRef scene, TexturePixel pixel, double currentZoom);
OverlayPixel? WorldToOverlay(MapSceneRef scene, WorldCoord world, double currentZoom);
WorldCoord? OverlayToWorld(MapSceneRef scene, OverlayPixel pixel, double currentZoom);
```

- [ ] **Step 3: Implement on `MapCalibrationService`**

```csharp
public TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom)
{
    var pick = PickTextureCalibration(scene);  // residual + min-ref-count + source-precedence — same as PickCalibration today
    return pick is null ? null : pick.Value.ToTexture(world, currentZoom);
}
// ... three more analogous methods
```

- [ ] **Step 4: Run tests** — green.

- [ ] **Step 5: Commit**
  ```bash
  git add src/Mithril.MapCalibration/IMapCalibrationService.cs \
          src/Mithril.MapCalibration/Internal/MapCalibrationService.cs \
          tests/Mithril.MapCalibration.Tests/MapCalibrationServiceTests.cs
  git commit -m "feat(map-calibration): add frame-explicit WorldToTexture/WorldToOverlay methods (#1076)"
  ```

### Task 2.5: `[Obsolete]` shims for old `WorldToWindow` / `WindowToWorld`

- [ ] **Step 1: Apply `[Obsolete]` to the existing interface methods**

```csharp
[Obsolete("Use WorldToTexture or WorldToOverlay; frame-explicit API since #1076.", error: false)]
PixelPoint? WorldToWindow(MapSceneRef scene, WorldCoord world, double currentZoom);

[Obsolete("Use TextureToWorld or OverlayToWorld; frame-explicit API since #1076.", error: false)]
WorldCoord? WindowToWorld(MapSceneRef scene, PixelPoint pixel, double currentZoom);
```

- [ ] **Step 2: Reimplement the shim on `MapCalibrationService`** to delegate:

```csharp
[Obsolete("...", error: false)]
public PixelPoint? WorldToWindow(MapSceneRef scene, WorldCoord world, double currentZoom)
{
    // Try texture first (the more-common case in pre-refactor callers); fall
    // back to overlay so Legolas pre-migration callers still resolve. PR 7
    // deletes this once all consumers migrate.
    if (WorldToTexture(scene, world, currentZoom) is { } tex)
        return new PixelPoint(tex.X, tex.Y);
    if (WorldToOverlay(scene, world, currentZoom) is { } ovr)
        return new PixelPoint(ovr.X, ovr.Y);
    return null;
}
```

- [ ] **Step 3: Suppress the obsoletion warning ONLY in test files that exercise the shim directly** — `#pragma warning disable CS0618` around each call. (The warning is the point everywhere else.)

- [ ] **Step 4: `dotnet build Mithril.slnx -warnaserror`** — expect a flood of CS0618 warnings from PR 3-5 territory. **STOP — do not silence them here.** Comment-document the expected residual warning count in the commit message; PRs 3-5 chew through them.

  Workaround for this PR's CI gate: temporarily allow CS0618 at the solution level (`<NoWarn>CS0618</NoWarn>` in `Directory.Build.props`) **only for the duration of this PR series** — and add a `// TODO(#1076): remove this NoWarn in PR 7` comment. Revert in PR 7.

- [ ] **Step 5: `dotnet test Mithril.slnx --nologo`** — green.

- [ ] **Step 6: Commit**
  ```bash
  git add src/Mithril.MapCalibration/IMapCalibrationService.cs \
          src/Mithril.MapCalibration/Internal/MapCalibrationService.cs \
          Directory.Build.props
  git commit -m "refactor(map-calibration): obsolete WorldToWindow/WindowToWorld with delegating shims (#1076)"
  ```

### Task 2.6: PR 2 verification + open PR

- [ ] **Step 1: `dotnet build Mithril.slnx`** — green.
- [ ] **Step 2: `dotnet test Mithril.slnx --nologo`** — all existing tests + new typed tests green.
- [ ] **Step 3: Confirm Mithril shell still launches** (DI cycle smoke — per memory `di_cycle_invisible_to_unit_tests`):
  ```bash
  dotnet run --project src/Mithril.Shell &
  # wait ~30s, check boot.log for "creating App" → ... → "host started"
  tail -50 "$LOCALAPPDATA/Mithril/Shell/logs/mithril-boot.log"
  ```
  Then close the shell.
- [ ] **Step 4: `git push -u origin pixel-frame-typing-pr2-migrate-core`**
- [ ] **Step 5: `gh pr create`** with summary referencing PR 1 and noting that Task 2.5's `NoWarn CS0618` is temporary through PR 6.

---

## Phase 3 — Migrate Detection + Capture, closes #1076 (PR 3)

**Branch:** `pixel-frame-typing-pr3-detection-capture`

**Depends on:** PR 2 merged.

**Acceptance:** The `TypedDetection.Anchor`, all refiners, `TypeAwareRansacSolver`, `AutoCalibrationEngine`, and the Capture diagnostics surface use the new typed pixel structs. The original #1076 drift-check bug naturally compiles correct after the migration; a new regression-marker test asserts the bug stays dead. PR closes #1076.

### Task 3.1: `TypedDetection.Anchor` becomes `CroppedFramePixel`

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/TypedDetection.cs`
- Modify: `src/Mithril.MapCalibration.Detection/TypeAwareRansacSolver.cs` (consumes `det.AnchorX/AnchorY`)
- Modify: tests in `tests/Mithril.MapCalibration.Tests/Detection/`

- [ ] **Step 1: Modify the record**

```csharp
public sealed record TypedDetection(
    string LandmarkType,
    string IconName,
    CroppedFramePixel Anchor,
    double Score);
```

- [ ] **Step 2: Update construction sites**
  ```bash
  rg -n "new TypedDetection\(" src/ tests/
  ```
  At each site, wrap `AnchorX, AnchorY` into `new CroppedFramePixel(x, y)`.

- [ ] **Step 3: Update `TypeAwareRansacSolver`**
  ```csharp
  // before:
  var (tx, ty) = mapRect.ScreenshotToTexture(det.AnchorX, det.AnchorY);
  // after:
  var tex = mapRect.CroppedToTexture(det.Anchor);
  ```
  *(The `ScreenshotToTexture(double, double)` legacy overload stays for one more PR; it gets removed in PR 7 once nothing references it.)*

- [ ] **Step 4: `dotnet build src/Mithril.MapCalibration.Detection`** — green.

- [ ] **Step 5: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~Detection"`** — green.

- [ ] **Step 6: Commit**
  ```bash
  git add src/Mithril.MapCalibration.Detection/TypedDetection.cs \
          src/Mithril.MapCalibration.Detection/TypeAwareRansacSolver.cs \
          tests/Mithril.MapCalibration.Tests/Detection/
  git commit -m "refactor(map-calibration): TypedDetection.Anchor is CroppedFramePixel (#1076)"
  ```

### Task 3.2: Refiner outputs use `LocateMetrics` Tx/Ty in `CapturedFramePixel` terms

**Files:**
- Modify: `src/Mithril.MapCalibration/LocateMetrics.cs`
- Modify: `src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs`
- Modify: `src/Mithril.MapCalibration.Detection/FeatureMatchingRefiner.cs`
- Modify: `src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs`

`LocateMetrics.Tx/Ty` currently hold the located-rect's origin in the captured frame. Add a typed accessor.

- [ ] **Step 1: Add typed accessor on `LocateMetrics`**

```csharp
public CapturedFramePixel LocatedRectOrigin => new(Tx, Ty);
```

- [ ] **Step 2: Confirm no behavioural change**
  ```bash
  dotnet test Mithril.slnx --filter "FullyQualifiedName~Refiner"
  ```
  Expected: green.

- [ ] **Step 3: Commit**
  ```bash
  git add src/Mithril.MapCalibration/LocateMetrics.cs
  git commit -m "refactor(map-calibration): add typed LocatedRectOrigin accessor on LocateMetrics (#1076)"
  ```

### Task 3.3: `AutoCalibrationEngine` drift-check fix — the #1076 closure

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs:243-314` (the `CheckDriftCoreAsync` body around the comparison block)

- [ ] **Step 1: Read the current state of `CheckDriftCoreAsync`**
  Specifically lines 243-314. Identify:
  - `clamped` (the located rect, `CapturedFramePixel`-ish today as raw `MapRect`),
  - `crop` (the cropped gray image),
  - `alignedRect` (the `MapRect` handed to the detector; origin `(0,0)`, crop-aligned),
  - `detections` (list of `TypedDetection`),
  - `references` (list of `LandmarkReference` with `World` coord),
  - `stored` (a `WorldToTextureCalibration` after PR 2 routing).

- [ ] **Step 2: Replace the buggy block with frame-typed comparison**

```csharp
// Build the LocatedMapRect that ties the crop-aligned alignedRect to its
// captured-frame placement. This is the new compile-time-enforced way to
// reason about "the located rect within the full frame" — formerly carried
// implicitly via loc.Tx/Ty being added to predictions.
var locatedRect = new LocatedMapRect(alignedRect, loc.LocatedRectOrigin);

var usedDetectionIndices = new HashSet<int>(detections.Count);
var residuals = new List<double>(references.Count);
foreach (var r in references)
{
    // Predict in TEXTURE space (where the stored calibration solves):
    TexturePixel predTex = stored.ToTexture(r.World, currentZoom: 1.0);

    // Convert to CROP space — same frame as TypedDetection.Anchor.
    CroppedFramePixel predCrop = alignedRect.TextureToCropped(predTex);

    double? best = null;
    int bestIdx = -1;
    for (int di = 0; di < detections.Count; di++)
    {
        if (usedDetectionIndices.Contains(di)) continue;
        var dist = predCrop.DistanceTo(detections[di].Anchor);  // ← type-safe, same frame
        if (dist < (best ?? double.MaxValue))
        {
            best = dist;
            bestIdx = di;
        }
    }
    if (best is null || best.Value > DriftMatchGatePx) continue;
    usedDetectionIndices.Add(bestIdx);
    residuals.Add(best.Value);
    _logger?.LogTrace(
        "Drift check {MapAssetKey}: ref '{Name}' predicted=({Px:0.0},{Py:0.0}), nearest detection=({Dx:0.0},{Dy:0.0}) at {Dist:0.00}px.",
        sceneRef.MapAssetKey, r.Name,
        predCrop.X, predCrop.Y,
        detections[bestIdx].Anchor.X, detections[bestIdx].Anchor.Y,
        best.Value);
}
```

Note what this rules out at compile time: any line of the form `predTex.DistanceTo(detection.Anchor)` no longer compiles — `TexturePixel.DistanceTo(TexturePixel)` only accepts texture-frame inputs; `detection.Anchor` is `CroppedFramePixel`. The author is forced to insert a frame conversion explicitly.

- [ ] **Step 3: Build**
  ```bash
  dotnet build src/Mithril.MapCalibration.Capture
  ```
  Expected: green.

- [ ] **Step 4: Run existing drift-check tests**
  ```bash
  dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~DriftCheck"
  ```
  Expected: green (existing tests fed pre-cropped inputs with `loc.Tx/Ty ≈ 0`, so they were always coincidentally correct).

- [ ] **Step 5: Commit**
  ```bash
  git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs
  git commit -m "fix(map-calibration): drift check uses crop-frame comparison, closes #1076"
  ```

### Task 3.4: Regression-marker test for #1076

**Files:**
- Create: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineDriftCheck1076RegressionTests.cs`

- [ ] **Step 1: Write the regression test**
  This is the missing fixture from PR #1064: a drift check where the located rect originates at a NON-ZERO position in the captured frame.

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public class AutoCalibrationEngineDriftCheck1076RegressionTests
{
    /// <summary>
    /// Regression marker for #1076. Pre-fix, the drift check produced
    /// reference predictions in CAPTURED-FRAME pixel coordinates but compared
    /// them against detections in CROP-FRAME coordinates — the mismatch
    /// equalled (loc.Tx, loc.Ty), and with DriftMatchGatePx=20 every reference
    /// fell outside the gate. Symptom: "inconclusive — 0 refs matched."
    ///
    /// Today's design rules this out at compile time (TexturePixel vs
    /// CroppedFramePixel are different types), but this fixture exercises the
    /// arithmetic on a captured frame whose located rect starts at non-zero
    /// position, so a future refactor that re-introduces the bug class
    /// (e.g. an `IPixelPoint`-erased shortcut) fails this test loudly.
    /// </summary>
    [Fact]
    public async Task DriftCheck_WithNonZeroLocateOffset_MatchesAtLeastDriftMinMatchedReferences()
    {
        // Synthesise a capture where the located rect originates at (320, 58) —
        // mirroring the live Map_KhyruleksCrypt 2026-06-04 attempt that exposed #1076.
        var fixture = EngineHarness.BuildDriftCheckScenario(
            locatedRectOriginXInCapturedFrame: 320,
            locatedRectOriginYInCapturedFrame: 58,
            cropWidth: 500,
            cropHeight: 700,
            textureWidth: 760,
            textureHeight: 1060,
            referencesVisibleInCapture: 5,
            storedCalibrationResidualPx: 0.53);

        var outcome = await fixture.Engine.CheckDriftAsync(fixture.SceneRef, CancellationToken.None);

        outcome.Should().BeOfType<DriftCheckOutcome.Ok>(
            "with a correct frame-typed comparison, refs match successfully and "
            + "residuals stay below the 3× tolerance threshold");
        ((DriftCheckOutcome.Ok)outcome).MatchedReferences.Should().BeGreaterThanOrEqualTo(3);
    }
}
```

*Note: `EngineHarness.BuildDriftCheckScenario` is a new helper for this test — synthesise a `CapturedFrame` + `LocateMetrics` + stored `WorldToTextureCalibration` + references such that the references reproject through the stored calibration into the synthesised detections. Implement it minimally in `EngineHarness.cs` alongside this test if not already present.*

- [ ] **Step 2: Run** — expect PASS.

- [ ] **Step 3: Sanity check** — temporarily revert Task 3.3's fix (reapply the `loc.Tx`/`loc.Ty` addition); rerun the test; confirm FAIL with "0 refs matched". Restore the fix.

- [ ] **Step 4: Commit**
  ```bash
  git add tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineDriftCheck1076RegressionTests.cs \
          tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineHarness.cs
  git commit -m "test(map-calibration): regression marker for #1076 drift-check crop/full-frame mix-up"
  ```

### Task 3.5: Sweep remaining Capture / Detection consumers

For every file in `src/Mithril.MapCalibration.Capture/` and `src/Mithril.MapCalibration.Detection/` that still references `PixelPoint`:

- [ ] **Step 1: Find them**
  ```bash
  rg -l "PixelPoint" src/Mithril.MapCalibration.Capture/ src/Mithril.MapCalibration.Detection/
  ```

- [ ] **Step 2: For each file**, apply the Phase 2 migration pattern:
  - Identify the frame from context.
  - Swap `PixelPoint` → `<Frame>Pixel`.
  - Insert any required conversions on `MapRect` / `LocatedMapRect`.
  - Test the project.
  - Commit per-file.

Expected file list (from spec §11 audit): `MapCalibrationSolveEngine.cs`, `NccTemplateMatch.cs`, `IconLikelihoodField.cs`, `CalibrationBundleJson.cs`, `FilesystemCalibrationAttemptBundleSink.cs`, `AttemptBundleVisualizer.cs`.

### Task 3.6: PR 3 verification + open PR + in-game smoke

- [ ] **Step 1: `dotnet build Mithril.slnx`** — green.
- [ ] **Step 2: `dotnet test Mithril.slnx --nologo`** — all green, regression marker passing.
- [ ] **Step 3: In-game smoke** (per spec §13).
  - Launch Mithril shell, launch PG, zone into a calibrated map (Map_KhyruleksCrypt if user's testbed) with a stored UserRefinement.
  - Press manual-calibrate hotkey.
  - Check log:
    ```bash
    rg -n "Drift check.*inconclusive" "$LOCALAPPDATA/Mithril/Shell/logs/mithril-*.json" | tail
    ```
    Expected: NO `inconclusive — too few visible landmarks (0 refs matched, need ≥3)` for the just-tested attempt.
  - Confirm the chip reports either "Calibration check OK" or "Drift detected — re-press to recalibrate." If "Inconclusive" appears for any other reason (foreground / hash gate / etc.), record it but do not block.
- [ ] **Step 4: `git push -u origin pixel-frame-typing-pr3-detection-capture`**
- [ ] **Step 5: `gh pr create`** — **set the PR body to close #1076 explicitly via `Closes #1076` so the issue auto-closes on merge.**

---

## Phase 4 — Migrate `Mithril.Overlay` (PR 4)

**Branch:** `pixel-frame-typing-pr4-overlay`

**Depends on:** PR 3 merged.

**Acceptance:** `IOverlaySceneContext.Project`, `IWorldOverlayMarkers`, `MarkerSceneRenderer`, `OverlayWindowService` use `OverlayPixel`. `Mithril.Overlay` projects no longer reference the deprecated `PixelPoint`.

### Migration pattern for Phase 4

Same as Phase 2's pattern. Every `PixelPoint` in `Mithril.Overlay` is an `OverlayPixel` (the project IS the overlay frame) — pure mechanical rename, no frame ambiguity inside the project. The boundary with Legolas (PR 5) is the only spot where a frame discussion might be needed; expect green compilation through PR 4 because Legolas still uses the old `PixelPoint` typedef which is binary-compatible at the call site (it's just an alias rename in `GlobalUsings.cs`).

### Tasks 4.0 – 4.6: Per-file migration

- [ ] **4.0:** Branch + baseline (as Phase 2 Task 2.0).
- [ ] **4.1:** `src/Mithril.Overlay/IOverlaySceneContext.cs` — replace `PixelPoint? Project` with `OverlayPixel? Project`. Update implementers. Build + test + commit.
- [ ] **4.2:** `src/Mithril.Overlay/IWorldOverlayMarkers.cs` — same pattern. Build + test + commit.
- [ ] **4.3:** `src/Mithril.Overlay/Internal/MarkerSceneRenderer.cs` — `MarkerDrawer` delegate signature changes from `PixelPoint pixel` to `OverlayPixel pixel`. Update all `RegisterDrawer<TStyle>` callers (Legolas marker drawers — see file list in spec §5). For Legolas drawers, change the parameter type to `OverlayPixel` in PR 4 ONLY for the `Mithril.Overlay`-facing signature; the rest of Legolas migrates in PR 5. Build + test + commit.
- [ ] **4.4:** `src/Mithril.Overlay/Internal/OverlayWindowService.cs` — `ProjectMarkers` return type from `IReadOnlyList<(PixelPoint, IMarkerStyle)>` to `IReadOnlyList<(OverlayPixel, IMarkerStyle)>`. Build + test + commit.
- [ ] **4.5:** Sweep any remaining `PixelPoint` in the project. `rg -l "PixelPoint" src/Mithril.Overlay/` → for each file: swap. Build + test + commit per-file.
- [ ] **4.6:** Verify + open PR — `dotnet build/test Mithril.slnx`, then `git push` + `gh pr create`. Reference PR 3.

---

## Phase 5 — Migrate `Legolas.Module` (PR 5, largest)

**Branch:** `pixel-frame-typing-pr5a-legolas-domain-services` and `pixel-frame-typing-pr5b-legolas-rendering-views` (split for review fatigue).

**Depends on:** PR 4 merged.

**Acceptance:** `Legolas.Module` no longer references `PixelPoint` directly. The `OverlayPixel` and `CanvasPixel` distinction is honoured at every mouse-event ↔ marker-position boundary. All Legolas tests still green; per-file fixture frame-statements updated per the P.3 audit.

### Migration pattern for Phase 5

The P.3 verification owed produced a per-test-file frame table. For each Legolas file (src + test):

1. Look up the implicit frame from the P.3 audit (or from surrounding context if not in the audit).
2. If the file describes overlay markers / rendering / world projections → `OverlayPixel`.
3. If the file describes mouse-event handlers / canvas clicks → `CanvasPixel`, with a `CanvasOverlayMapping` conversion at the boundary where the value flows into rendering code.
4. If the file describes world-frame-only operations (e.g. solver inputs) and just happens to hold a 2D pixel as a tuple, decide whether the pixel side is overlay (most cases) or canvas (mouse-derived).
5. Apply the rename.
6. Test the affected project.
7. Commit per-file.

### Phase 5a: domain + services + view models + view models tests

- [ ] **5a.0:** Branch + baseline (`pixel-frame-typing-pr5a-legolas-domain-services`).
- [ ] **5a.1:** `src/Legolas.Module/GlobalUsings.cs` — remove the legacy `global using PixelPoint = …` alias. Replace with imports for the new frame types as needed. (Build will go red until 5a.2+ rewrite consumers; per-step rolling fixes from here on.)
- [ ] **5a.2:** Domain types (`Survey.cs`, `GhostMarker.cs`, `WedgeArc.cs`, `PinScene.cs`, `MotherlodeGuidanceCircle.cs`). Each → `OverlayPixel`. Build the project per-file; commit per-file.
- [ ] **5a.3:** Services (`CoordinateProjector.cs`, `AdaptiveRouteOptimizer.cs`, `AreaCalibrationService.cs`, `MotherlodeMeasurementCoordinator.cs`, `PinCalibrationCoordinator.cs`, `MultilaterationSolver.cs`, `PlayerLogIngestionService.cs`). Frame is overlay for projection outputs, canvas for click-origin inputs. Build per-file; commit per-file.
- [ ] **5a.4:** ViewModels (`CalibrationSessionViewModel.cs`, `MapOverlayViewModel.cs`, `MotherlodeViewModel.cs`, `LegolasWizardViewModel.cs`, others). Same pattern. Build per-file; commit per-file.
- [ ] **5a.5:** Tests covering 5a.2 / 5a.3 / 5a.4. Numeric fixtures stay as-is; wrap in the correct frame struct per P.3 audit. Run `dotnet test tests/Legolas.Tests` — green. Commit.
- [ ] **5a.6:** Verify + open PR 5a.

### Phase 5b: rendering + views + hotkeys + diagnostics

- [ ] **5b.0:** Branch from PR 5a once merged.
- [ ] **5b.1:** Rendering drawers (`LegolasCalibrationMarkerDrawer.cs`, `LegolasMotherlodeMarkerDrawer.cs`, `LegolasMotherlodeGuidanceMarkerDrawer.cs`, `LegolasMarkerDrawerCore.cs`, `LegolasPlayerMarkerDrawer.cs`, `LegolasSurveyMarkerDrawer.cs`, `LegolasOverlaySceneDrawer.cs`). All → `OverlayPixel` parameter. Build per-file; commit per-file.
- [ ] **5b.2:** Views (`MapOverlayView.xaml.cs`, `CalibrationOverlayView.xaml.cs`). These compute `Mouse.GetPosition(canvas)` → `CanvasPixel`, then go through `CanvasOverlayMapping.CanvasToOverlay` before feeding into rendering code. Insert the conversion at each mouse-event handler boundary.

  Example transformation:
  ```csharp
  // before (MapOverlayView.xaml.cs:296):
  var clickPoint = new PixelPoint(canvasPos.X, canvasPos.Y);
  vm.OnMapClicked(clickPoint);

  // after:
  var clickCanvas = new CanvasPixel(canvasPos.X, canvasPos.Y);
  var clickOverlay = _canvasOverlayMapping.CanvasToOverlay(clickCanvas);
  vm.OnMapClicked(clickOverlay);
  // (vm.OnMapClicked's parameter type changes to OverlayPixel in 5a.4)
  ```

  The `CanvasOverlayMapping` instance can come from a per-view DI'd singleton (today: hard-code DPI=1; PR follow-up sources real DPI). Build per-file; commit per-file.
- [ ] **5b.3:** Hotkeys (`OverlayController.cs`). Same canvas → overlay conversion pattern at each `canvasPos` site. Build + commit per-file.
- [ ] **5b.4:** Diagnostics (`SurveyPerfHarness.cs`). Overlay frame. Build + commit.
- [ ] **5b.5:** Tests covering 5b.1-5b.4. Run `dotnet test tests/Legolas.Tests` — green. Commit.
- [ ] **5b.6:** Verify + open PR 5b.

---

## Phase 6 — Persistence-schema migration (PR 6)

**Branch:** `pixel-frame-typing-pr6-persistence-schema`

**Depends on:** PR 5b merged.

**Acceptance:** `AreaCalibration` JSON SchemaVersion 2 ships with explicit `frame` field on new writes; Schema-1 records load via the §7.2 provenance fallback. Round-trip tests cover all four `Source` values + an unknown-source forward-compat case + the file-of-origin disambiguation for `Source: UserRefinement`.

### Task 6.0: Branch + baseline
- [ ] Branch from main.
- [ ] Baseline build + test.

### Task 6.1: Add `frame` to JSON schema

**Files:**
- Modify: `src/Mithril.MapCalibration/AreaCalibration.cs` — add `public CalibrationFrame Frame { get; init; }` field with `[JsonPropertyName("frame")]`.
- Create: `src/Mithril.MapCalibration/CalibrationFrame.cs` — `public enum CalibrationFrame { Texture, Overlay }`.
- Modify: relevant `[JsonSerializable]` contexts to include the enum and updated record.

- [ ] **Step 1: Write the failing round-trip test**

```csharp
[Fact]
public void Schema2_RoundTripsFrameField()
{
    var record = new AreaCalibration { /* ...fields..., Frame = CalibrationFrame.Overlay */ };
    var json = JsonSerializer.Serialize(record, JsonContext.Default.AreaCalibration);
    var roundTrip = JsonSerializer.Deserialize<AreaCalibration>(json, JsonContext.Default.AreaCalibration);
    roundTrip!.Frame.Should().Be(CalibrationFrame.Overlay);
}
```

- [ ] **Step 2: Add the enum + field, regenerate sourcegen.** Build + run test → green.

- [ ] **Step 3: Commit.**

### Task 6.2: Schema-1 load-time provenance fallback

**Files:**
- Modify: the loaders that read `AreaCalibration` JSON (`UserRefinementStore`, community-calibration loader, baseline loader, `LegolasSettings` loader).

- [ ] **Step 1: Write the failing test (per `Source` value × per file-of-origin)**

```csharp
[Theory]
[InlineData(CalibrationSource.AutoCapture, /*from*/ "UserRefinementStore", CalibrationFrame.Texture)]
[InlineData(CalibrationSource.UserRefinement, "UserRefinementStore", CalibrationFrame.Texture)]
[InlineData(CalibrationSource.UserRefinement, "LegolasSettings.AreaCalibrations", CalibrationFrame.Overlay)]
[InlineData(CalibrationSource.CommunitySync, "UserRefinementStore", CalibrationFrame.Texture)]
[InlineData(CalibrationSource.BundledBaseline, "UserRefinementStore", CalibrationFrame.Texture)]
public void Schema1_Load_InfersFrame_FromSourceAndFileOfOrigin(
    CalibrationSource source, string fileOfOrigin, CalibrationFrame expectedFrame)
{
    var record = LoadSchema1Record(source, fileOfOrigin);
    record.Frame.Should().Be(expectedFrame);
}

[Fact]
public void Schema1_UnknownSource_DefaultsToTexture_WithWarnLog()
{
    var record = LoadSchema1Record(/* source: */ "FutureSource_DoesNotExist", "UserRefinementStore");
    record.Frame.Should().Be(CalibrationFrame.Texture);
    // ... assert the warn log fired
}
```

- [ ] **Step 2: Implement the load-time inference** in each loader.

- [ ] **Step 3: Run** — green. **Commit.**

### Task 6.3: PR 6 verification + open PR

- [ ] Build + full test suite + manual: spot-check load of a real LocalLow profile (load `%LocalAppData%/Mithril/MapCalibration/refinements.json`; assert inferred frames match the documented convention).
- [ ] Push + open PR.

---

## Phase 7 — Tombstone (PR 7)

**Branch:** `pixel-frame-typing-pr7-tombstone`

**Depends on:** PR 6 merged.

**Acceptance:** Obsolete `PixelPoint`, `AreaCalibration`, deprecated `IMapCalibrationService` methods, and legacy `MapRect.ScreenshotToTexture(double, double)` overload are all deleted. The `NoWarn CS0618` workaround from PR 2 Task 2.5 is reverted. No remaining consumers of the deleted types anywhere in the repo.

### Tasks 7.0 – 7.5

- [ ] **7.0:** Branch + baseline build + test.
- [ ] **7.1:** Grep-confirm zero references:
  ```bash
  rg -n "PixelPoint" src/ tests/  # expect: nothing in src (only WorldCoord/<Frame>Pixel)
  rg -n "AreaCalibration[^A-Za-z]" src/ tests/  # expect: only within the AreaCalibration.cs file itself + tests we're about to delete
  rg -n "WorldToWindow\|WindowToWorld" src/ tests/  # expect: zero
  rg -n "ScreenshotToTexture\(double" src/ tests/  # expect: zero
  ```
  Each `rg` must return empty. If not, the migration missed a site; back up and fix.

- [ ] **7.2:** Delete `src/Mithril.MapCalibration/PixelPoint.cs`. Build + test — green. Commit.
- [ ] **7.3:** Delete `src/Mithril.MapCalibration/AreaCalibration.cs`. Build + test — green. Commit.
- [ ] **7.4:** Remove the `[Obsolete]` methods on `IMapCalibrationService` and their `MapCalibrationService` implementations. Remove the legacy `Dictionary<MapSceneRef, IReadOnlyList<AreaCalibration>>` storage. Build + test — green. Commit.
- [ ] **7.5:** Remove the legacy `MapRect.ScreenshotToTexture(double, double)` method. Build + test — green. Commit.
- [ ] **7.6:** Revert the `<NoWarn>CS0618</NoWarn>` line from `Directory.Build.props` (no obsolete-marked types left). Build with `-warnaserror` to confirm. Commit.
- [ ] **7.7:** Flip the `spec.md` INDEX row from `active` to `shipped`. Commit.
- [ ] **7.8:** Push + open PR 7. PR body notes: "Final cleanup; no remaining consumers of deleted types."

---

## Self-review

Run after the plan is fully written; fix issues inline.

**1. Spec coverage:** every spec section maps to at least one plan task.
- §1 Goal/non-goals → Phase 1-7 collectively.
- §2 Problem statement → Phase 3 fixes the catalyst bug (Task 3.3) + regression marker (Task 3.4).
- §3 Frame enumeration → Tasks 1.2-1.7 introduce each frame.
- §4 Core types → Tasks 1.1-1.7.
- §5 Cross-frame conversion table → Tasks 1.10-1.14 and §5.1's `LocatedMapRect` → Task 1.12.
- §6 AreaCalibration split → Tasks 1.8-1.10.
- §7 Persistence → Phase 6.
- §8 What this rules out → Task 3.3's "Note what this rules out at compile time" + Task 3.4's regression test.
- §9 Migration phasing → Phases 1-7 mirror PRs 1-7.
- §10 Test strategy → distributed across Phase 1's per-type tests + Task 3.4 regression marker + Phase 6's round-trip tests + Phase 7's grep-verify.
- §11 Risk surface → preflight Tasks P.1-P.4 close the load-bearing assumptions.
- §12 Out of scope → respected; no extra work proposed.
- §13 Verification owed → preflight P.1-P.4 + Task 3.6 in-game smoke.

✅ No coverage gaps.

**2. Placeholder scan:** searched for TBD/TODO/etc.
- One literal `TODO(#1076)` in Task 2.5's `NoWarn` comment — intentional, tracks the planned PR 7 revert. ✅ Acceptable.
- No "fill in details" / "similar to Task N" / step-without-code instances. ✅

**3. Type consistency:**
- `TexturePixel` / `CapturedFramePixel` / `CroppedFramePixel` / `OverlayPixel` / `CanvasPixel` / `GameWindowPixel` — used identically throughout. ✅
- `WorldToTextureCalibration.ToTexture` / `FromTexture` and `WorldToOverlayCalibration.ToOverlay` / `FromOverlay` — used consistently. ✅
- `MapRect.CroppedToTexture` / `TextureToCropped` vs `LocatedMapRect.CroppedToCaptured` / `CapturedToCropped` — distinct method names, distinct types. ✅
- `IPixelPoint` — only mentioned at definition (Task 1.1) and verified at use (Task 1.2's `ImplementsIPixelPoint` test). ✅
- `CanvasOverlayMapping.CanvasToOverlay` / `OverlayToCanvas` — used in Task 5b.2 mouse handlers + Task 1.14 tests. Consistent. ✅
- `LocateMetrics.LocatedRectOrigin` (new accessor in Task 3.2) — used in Task 3.3's `var locatedRect = new LocatedMapRect(alignedRect, loc.LocatedRectOrigin);`. Consistent. ✅

No issues found. Plan is internally consistent.
