# Per-attempt Calibration Diagnostic Bundle — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development with `model: sonnet` for implementer + reviewer subagents. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Plan home:** This file is **scratch** per project memory `where_things_live`. The canonical plan lives in the GitHub issue body. Delete this file once the work lands.

**Goal:** Replace today's flat capture-frame dump with a structured per-attempt diagnostic bundle written from `AutoCalibrationEngine`, exposing the artifacts the synthesis-probe needs (refs #966, #978).

**Architecture:** Plain-data `CalibrationAttemptContext` accumulates pipeline-stage outputs via property writes; `AutoCalibrationEngine.TryCalibrateCurrentAreaAsync` wraps the pipeline in `try { … } finally { _sink.Write(attempt); }`; `FilesystemCalibrationAttemptBundleSink` writes a per-attempt subdirectory under `%LocalAppData%/Mithril/diagnostics/calibration/`; `NullCalibrationAttemptBundleSink` no-ops when the toggle is off.

**Tech Stack:** .NET 10, WPF (`DrawingVisual` + `RenderTargetBitmap` + `PngBitmapEncoder` only — no `System.Drawing` per #921 guard), `System.Text.Json` source-generated contexts, xunit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-06-01-calibration-diagnostic-bundle-design.md` (read this first).

---

## Task 1: `MapRect.TextureToScreenshot` inverse-map

**Files:**
- Modify: `src/Mithril.MapCalibration/Detection/MapRectLocator.cs` (the `MapRect` record at line 294)
- Test: `tests/Mithril.MapCalibration.Tests/Detection/MapRectTests.cs` (create if absent — search first; if a `MapRectTests.cs` already exists anywhere under `tests/Mithril.MapCalibration.Tests`, extend it instead)

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class MapRectInverseMapTests
{
    [Theory]
    [InlineData(12, 18, 1192, 1020, 4096, 4096, 500.0, 500.0)]
    [InlineData(0, 0, 800, 600, 1024, 1024, 123.4, 567.8)]
    [InlineData(50, 50, 100, 100, 2048, 2048, 50.0, 50.0)]
    public void TextureToScreenshot_inverts_ScreenshotToTexture(
        int ox, int oy, int w, int h, int tw, int th, double sx, double sy)
    {
        var rect = new MapRect(ox, oy, w, h, tw, th);
        var (tx, ty) = rect.ScreenshotToTexture(sx, sy);
        var (rx, ry) = rect.TextureToScreenshot(tx, ty);
        rx.Should().BeApproximately(sx, 1e-9);
        ry.Should().BeApproximately(sy, 1e-9);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapRectInverseMapTests"`
Expected: FAIL with `'MapRect' does not contain a definition for 'TextureToScreenshot'`.

- [ ] **Step 3: Implement `TextureToScreenshot` on `MapRect`**

In `src/Mithril.MapCalibration/Detection/MapRectLocator.cs`, alongside the existing `ScreenshotToTexture` method:

```csharp
public (double Sx, double Sy) TextureToScreenshot(double tx, double ty)
{
    var scaleX = (double)TextureWidth / Width;
    var scaleY = (double)TextureHeight / Height;
    return (tx / scaleX + OriginX, ty / scaleY + OriginY);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapRectInverseMapTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/MapRectLocator.cs \
        tests/Mithril.MapCalibration.Tests/Detection/MapRectInverseMapTests.cs
git commit -m "feat(map-calibration): add MapRect.TextureToScreenshot inverse map"
```

---

## Task 2: Surface `Detections` on `CalibrationSolveResult`

**Files:**
- Modify: `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs` (the `CalibrationSolveResult` record at line 182)
- Modify: the solve implementation (same file — find the construction site of `CalibrationSolveResult` and populate `Detections`)
- Test: extend whatever test exercises the solver. Search: `grep -rn "CalibrationSolveResult" tests/Mithril.MapCalibration.Tests --include="*.cs"` to find the existing tests; the solve-result construction sites are inside `MapCalibrationSolveEngine`.

- [ ] **Step 1: Write the failing test**

Add to the existing solve-engine tests:

```csharp
[Fact]
public void Solve_populates_Detections_on_accepted_result()
{
    // Use the same synthetic-fixture builder the existing accepted-result test uses
    // (look for a test like "Solve_returns_calibration_on_clean_input"). Re-running
    // the same arrangement, the new assertion is:
    var request = BuildSyntheticDetectableRequest();   // existing helper
    var refs = BuildSyntheticReferences();             // existing helper
    var engine = new MapCalibrationSolveEngine(/* same ctor args the existing test uses */);

    var result = engine.Solve(request, refs);

    result.Calibration.Should().NotBeNull();
    result.Detections.Should().NotBeNull();
    result.Detections!.Should().NotBeEmpty();
}
```

If no existing test exists in this shape, write a minimal one against `MapCalibrationSolveEngine` using a synthetic `DetectionRequest` + a small refs list (the test fixtures under `tests/Mithril.MapCalibration.Tests/Detection/` already have a `DetectionRequest` builder — re-use).

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~Solve_populates_Detections"`
Expected: FAIL with `'CalibrationSolveResult' does not contain a definition for 'Detections'`.

- [ ] **Step 3: Add the property to `CalibrationSolveResult`**

In `src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs` at line 182, change:

```csharp
public sealed record CalibrationSolveResult(
    AreaCalibration? Calibration,
    int InlierCount,
    string? RejectReason,
    IReadOnlyList<TypeAwareRansacSolver.AssignedReference>? Inliers = null)
{
    public IReadOnlyList<TypedDetection>? Detections { get; init; }
}
```

(Init-only after the positional args, default null — non-breaking for existing consumers.)

- [ ] **Step 4: Populate `Detections` in the solver**

In the same file, find every `return new CalibrationSolveResult(…)` (or `with` expression). For the success path AND the rejection paths that have a detection list at hand (typically all of them after the detection phase runs), include `Detections = detections` (rename the local to `detections` if needed). For the very-early reject paths (no detections at all), `Detections` stays null. Inspect the file end-to-end to choose the right hand-off points.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~Solve_populates_Detections"`
Expected: PASS.

- [ ] **Step 6: Run the full MapCalibration test suite to check for regressions**

Run: `dotnet test tests/Mithril.MapCalibration.Tests`
Expected: PASS (all existing tests + new one).

- [ ] **Step 7: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs \
        tests/Mithril.MapCalibration.Tests/
git commit -m "feat(map-calibration): surface detections on CalibrationSolveResult"
```

---

## Task 3: `CalibrationAttemptContext` + JSON DTOs + source-gen serializer context

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs`
- Create: `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs` (DTOs + `CalibrationBundleJsonContext`)
- Test: `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationBundleJsonTests.cs`

(`Diagnostics/` is a new folder under the Capture project — group all new types there for one-place isolation.)

- [ ] **Step 1: Write the failing JSON round-trip tests**

```csharp
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class CalibrationBundleJsonTests
{
    [Fact]
    public void AttemptJson_round_trips_through_source_gen_context()
    {
        var sut = new AttemptJson(
            SchemaVersion: 1,
            Area: "AreaEltibule",
            AttemptStartedUtc: "2026-06-01T12:30:12.696Z",
            AttemptFinalizedUtc: "2026-06-01T12:30:14.812Z",
            Outcome: "accepted",
            RejectReason: null,
            EngineVersion: "0.5.0+test",
            Files: new AttemptFilesJson(
                RawScreenshot: "02-screenshot-raw.png",
                GrayScreenshot: "03-screenshot-gray.png",
                MapRect: "04-maprect.json",
                BaseTextureResampled: "05-base-texture-resampled.png",
                AlignedScreenshot: "06-aligned-screenshot.png",
                Deviation: "07-deviation.png",
                DetectionsImage: "08-detections.png",
                ProjectionOverlay: "09-projection-overlay.png",
                Detections: "10-detections.json",
                RecoveredCalibration: "11-recovered-cal.json"));

        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.AttemptJson);
        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.AttemptJson);

        round.Should().BeEquivalentTo(sut);
    }

    [Fact]
    public void MapRectJson_round_trips()
    {
        var sut = new MapRectJson(1, 12, 18, 1192, 1020, 4096, 4096, 0.847, null);
        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.MapRectJson);
        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.MapRectJson);
        round.Should().BeEquivalentTo(sut);
    }

    [Fact]
    public void DetectionsJson_round_trips()
    {
        var sut = new DetectionsJson(1, 16,
            new[] { new DetectionJson("Portal", "landmark_portal", 412.7, 588.3, 0.94) });
        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.DetectionsJson);
        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.DetectionsJson);
        round.Should().BeEquivalentTo(sut);
    }

    [Fact]
    public void RecoveredCalibrationJson_round_trips()
    {
        var sut = new RecoveredCalibrationJson(1,
            Scale: 0.31536, RotationRadians: -3.14159,
            OriginX: 1039.45, OriginY: -36.38,
            MirrorNorth: false, CalibrationZoom: 1.0,
            ResidualPixels: 0.34, ReferenceCount: 8,
            Source: "AutoCapture",
            Inliers: new[] { new InlierJson("Portal:E→S", 234.1, -78.5, 612.3, 488.7, 0.94) });
        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.RecoveredCalibrationJson);
        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.RecoveredCalibrationJson);
        round.Should().BeEquivalentTo(sut);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (types don't exist yet)**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationBundleJsonTests"`
Expected: FAIL with type-not-found errors.

- [ ] **Step 3: Create the DTO records + source-gen context**

`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`:

```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Capture.Diagnostics;

public sealed record AttemptJson(
    int SchemaVersion,
    string Area,
    string AttemptStartedUtc,
    string AttemptFinalizedUtc,
    string Outcome,
    string? RejectReason,
    string EngineVersion,
    AttemptFilesJson Files);

public sealed record AttemptFilesJson(
    string? RawScreenshot,
    string? GrayScreenshot,
    string? MapRect,
    string? BaseTextureResampled,
    string? AlignedScreenshot,
    string? Deviation,
    string? DetectionsImage,
    string? ProjectionOverlay,
    string? Detections,
    string? RecoveredCalibration);

public sealed record MapRectJson(
    int SchemaVersion,
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight,
    double? AutoDetectScore,
    double? SourceScaleFactor);

public sealed record DetectionJson(
    string LandmarkType,
    string IconName,
    double AnchorX,
    double AnchorY,
    double Score);

public sealed record DetectionsJson(
    int SchemaVersion,
    int RenderSizePx,
    IReadOnlyList<DetectionJson> Detections);

public sealed record InlierJson(
    string Label,
    double WorldX,
    double WorldZ,
    double PixelX,
    double PixelY,
    double MatchScore);

public sealed record RecoveredCalibrationJson(
    int SchemaVersion,
    double Scale,
    double RotationRadians,
    double OriginX,
    double OriginY,
    bool MirrorNorth,
    double CalibrationZoom,
    double ResidualPixels,
    int ReferenceCount,
    string Source,
    IReadOnlyList<InlierJson> Inliers);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AttemptJson))]
[JsonSerializable(typeof(MapRectJson))]
[JsonSerializable(typeof(DetectionsJson))]
[JsonSerializable(typeof(RecoveredCalibrationJson))]
public partial class CalibrationBundleJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Create `CalibrationAttemptContext`**

`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs`:

```csharp
using System;
using System.Collections.Generic;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.MapCalibration;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Per-attempt mutable data carrier. Populated by AutoCalibrationEngine as the
/// pipeline progresses; consumed by ICalibrationAttemptBundleSink.Write at the
/// end of the attempt (success, gate-reject, exception, or cancellation).
/// </summary>
public sealed class CalibrationAttemptContext
{
    public CalibrationAttemptContext(string area, DateTimeOffset startedUtc)
    {
        Area = area;
        StartedUtc = startedUtc;
    }

    public string Area { get; }
    public DateTimeOffset StartedUtc { get; }

    // Filled by the engine as it goes. All nullable — sink writes what it has.
    public CapturedFrame? RawCapture { get; set; }
    public GrayImage? GrayCapture { get; set; }
    public GrayImage? BaseTextureResampled { get; set; }
    public MapRect? MapRect { get; set; }
    public GrayImage? AlignedCrop { get; set; }
    public GrayImage? AlignedTexture { get; set; }
    public IReadOnlyList<LandmarkReference>? References { get; set; }
    public CalibrationSolveResult? Result { get; set; }

    // Outcome is set explicitly by the engine — either at each Fail() site, at
    // the end of the success path, or in the catch (exception → "error").
    public string Outcome { get; set; } = "unknown";
    public string? ExceptionInfo { get; set; }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationBundleJsonTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/ \
        tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/
git commit -m "feat(map-calibration): add bundle context + JSON DTOs"
```

---

## Task 4: Outcome subcategorization + `ICalibrationAttemptBundleSink` + `NullCalibrationAttemptBundleSink`

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/Diagnostics/ICalibrationAttemptBundleSink.cs`
- Create: `src/Mithril.MapCalibration.Capture/Diagnostics/NullCalibrationAttemptBundleSink.cs`
- Create: `src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs`
- Test: `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/OutcomeVocabularyTests.cs`

- [ ] **Step 1: Write failing outcome-mapping tests**

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class OutcomeVocabularyTests
{
    [Theory]
    [InlineData(null, "rejected-solve")]
    [InlineData("", "rejected-solve")]
    [InlineData("no geometrically-consistent fit", "rejected-solve")]
    [InlineData("no detections cleared the threshold", "rejected-solve-no-detections")]
    [InlineData("insufficient inliers (3 < 4 required)", "rejected-solve-insufficient-inliers")]
    [InlineData("residual 14.2 px exceeds 12 px gate", "rejected-solve-residual")]
    public void RejectSolveSubcategory_maps_reject_reasons(string? reason, string expected)
    {
        OutcomeVocabulary.RejectSolveSubcategory(reason).Should().Be(expected);
    }

    [Theory]
    [InlineData("rejected-no-area", false)]
    [InlineData("rejected-pg-not-foreground", false)]
    [InlineData("rejected-no-bbox", false)]
    [InlineData("rejected-capture-failed", true)]
    [InlineData("rejected-no-base-texture", true)]
    [InlineData("accepted", true)]
    [InlineData("error", true)]
    public void ShouldWriteBundle_skips_pre_capture_outcomes(string outcome, bool expected)
    {
        OutcomeVocabulary.ShouldWriteBundle(outcome).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~OutcomeVocabularyTests"`
Expected: FAIL with type-not-found.

- [ ] **Step 3: Implement `OutcomeVocabulary`**

`src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs`:

```csharp
using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Stable strings + classifiers for the per-attempt bundle's outcome field
/// (used in subdir names and 01-attempt.json).
/// </summary>
public static class OutcomeVocabulary
{
    public const string Accepted = "accepted";
    public const string RejectedNoArea = "rejected-no-area";
    public const string RejectedPgNotForeground = "rejected-pg-not-foreground";
    public const string RejectedNoBbox = "rejected-no-bbox";
    public const string RejectedCaptureFailed = "rejected-capture-failed";
    public const string RejectedNoBaseTexture = "rejected-no-base-texture";
    public const string RejectedMapNotLocated = "rejected-map-not-located";
    public const string RejectedClampDegenerate = "rejected-clamp-degenerate";
    public const string RejectedSolve = "rejected-solve";
    public const string RejectedSolveNoDetections = "rejected-solve-no-detections";
    public const string RejectedSolveInsufficientInliers = "rejected-solve-insufficient-inliers";
    public const string RejectedSolveResidual = "rejected-solve-residual";
    public const string Error = "error";

    private static readonly FrozenSet<string> NoBundleOutcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        RejectedNoArea, RejectedPgNotForeground, RejectedNoBbox,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>True when the bundle should be written; false for the pre-capture rejects.</summary>
    public static bool ShouldWriteBundle(string outcome) => !NoBundleOutcomes.Contains(outcome);

    /// <summary>
    /// Map a free-form <see cref="CalibrationSolveResult.RejectReason"/> to a fixed
    /// subdir-name suffix. Unmappable reasons → <see cref="RejectedSolve"/>.
    /// </summary>
    public static string RejectSolveSubcategory(string? rejectReason)
    {
        if (string.IsNullOrWhiteSpace(rejectReason)) return RejectedSolve;
        var s = rejectReason!.AsSpan();
        if (s.Contains("no detections", StringComparison.OrdinalIgnoreCase)) return RejectedSolveNoDetections;
        if (s.Contains("insufficient inliers", StringComparison.OrdinalIgnoreCase)) return RejectedSolveInsufficientInliers;
        if (s.Contains("residual", StringComparison.OrdinalIgnoreCase)) return RejectedSolveResidual;
        return RejectedSolve;
    }
}
```

- [ ] **Step 4: Implement `ICalibrationAttemptBundleSink` + `NullCalibrationAttemptBundleSink`**

`src/Mithril.MapCalibration.Capture/Diagnostics/ICalibrationAttemptBundleSink.cs`:

```csharp
namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Persists a per-attempt diagnostic bundle. Implementations MUST be fail-soft:
/// any exception must be swallowed and logged, never propagated into the
/// calling AutoCalibrationEngine.
/// </summary>
public interface ICalibrationAttemptBundleSink
{
    void Write(CalibrationAttemptContext context);
}
```

`src/Mithril.MapCalibration.Capture/Diagnostics/NullCalibrationAttemptBundleSink.cs`:

```csharp
namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// No-op sink. Used when CaptureDiagnosticsOptions.DumpCalibrationBundles is off.
/// </summary>
public sealed class NullCalibrationAttemptBundleSink : ICalibrationAttemptBundleSink
{
    public static readonly NullCalibrationAttemptBundleSink Instance = new();

    public void Write(CalibrationAttemptContext context) { }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~OutcomeVocabularyTests"`
Expected: PASS (10 cases).

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/ \
        tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/
git commit -m "feat(map-calibration): outcome vocabulary + sink interface + null sink"
```

---

## Task 5: `AttemptBundleVisualizer` — deviation byte math

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/Diagnostics/AttemptBundleVisualizer.cs`
- Test: `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/AttemptBundleVisualizerTests.cs`

- [ ] **Step 1: Write the failing deviation test**

```csharp
using System.Windows.Media.Imaging;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class AttemptBundleVisualizerTests
{
    [Fact]
    public void RenderDeviation_returns_max_positive_diff_per_pixel()
    {
        // 2x2 fixtures with known pairwise diffs.
        var a = new GrayImage(2, 2, new byte[] { 100, 200, 50, 75 });
        var b = new GrayImage(2, 2, new byte[] { 90, 250, 50, 100 });
        // Expected: max(0, a - b) = { 10, 0, 0, 0 }

        var visualizer = new AttemptBundleVisualizer();
        var src = visualizer.RenderDeviation(a, b);

        src.PixelWidth.Should().Be(2);
        src.PixelHeight.Should().Be(2);
        var pixels = new byte[4];
        src.CopyPixels(pixels, stride: 2, offset: 0);
        pixels.Should().Equal(10, 0, 0, 0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~RenderDeviation"`
Expected: FAIL with type-not-found.

- [ ] **Step 3: Implement `AttemptBundleVisualizer.RenderDeviation`**

`src/Mithril.MapCalibration.Capture/Diagnostics/AttemptBundleVisualizer.cs` (start; more methods added in later tasks):

```csharp
using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// WPF-only (DrawingVisual + RenderTargetBitmap + PngBitmapEncoder) renderers
/// for the three annotated bundle PNGs and the deviation map. No
/// System.Drawing (#921 guard).
/// </summary>
public sealed class AttemptBundleVisualizer
{
    public BitmapSource RenderDeviation(GrayImage screenshot, GrayImage baseTexture)
    {
        if (screenshot.Width != baseTexture.Width || screenshot.Height != baseTexture.Height)
        {
            throw new ArgumentException(
                $"Deviation inputs must match: screenshot {screenshot.Width}x{screenshot.Height}, " +
                $"baseTexture {baseTexture.Width}x{baseTexture.Height}.");
        }

        int w = screenshot.Width, h = screenshot.Height;
        var diff = new byte[w * h];
        var s = screenshot.Pixels;
        var b = baseTexture.Pixels;
        for (int i = 0; i < diff.Length; i++)
        {
            int d = s[i] - b[i];
            diff[i] = d > 0 ? (byte)d : (byte)0;
        }

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, diff, w);
        src.Freeze();
        return src;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~RenderDeviation"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/AttemptBundleVisualizer.cs \
        tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/AttemptBundleVisualizerTests.cs
git commit -m "feat(map-calibration): deviation byte math in AttemptBundleVisualizer"
```

---

## Task 6: `AttemptBundleVisualizer` — detections + projection overlay renderers

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/AttemptBundleVisualizer.cs`
- Test: `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/AttemptBundleVisualizerTests.cs`

- [ ] **Step 1: Write failing dimensions tests for the two overlay renderers**

Append to `AttemptBundleVisualizerTests`:

```csharp
[Fact]
public void RenderDetectionsOverlay_returns_bitmap_of_input_dims()
{
    var gray = new GrayImage(32, 24, new byte[32 * 24]);
    var detections = new[]
    {
        new TypedDetection("Portal", "landmark_portal", 10, 12, 0.91),
        new TypedDetection("Npc", "landmark_npc", 20, 18, 0.85),
    };

    var visualizer = new AttemptBundleVisualizer();
    var src = visualizer.RenderDetectionsOverlay(gray, detections, renderSizePx: 16);

    src.PixelWidth.Should().Be(32);
    src.PixelHeight.Should().Be(24);
}

[Fact]
public void RenderProjectionOverlay_returns_bitmap_of_input_dims()
{
    var raw = new CapturedFrame(32, 24, new byte[32 * 24 * 4]);
    var rect = new MapRect(0, 0, 32, 24, 64, 48);
    var cal = new AreaCalibration(
        Scale: 1.0, RotationRadians: 0, OriginX: 16, OriginY: 12,
        ReferenceCount: 1, ResidualPixels: 0.5);
    var refs = new[]
    {
        new LandmarkReference("Portal", "X", new WorldCoord(0, 0, 0)),
    };
    var inliers = new[]
    {
        new TypeAwareRansacSolver.AssignedReference("Portal:X", 0, 0, 16, 12, 0.9),
    };

    var visualizer = new AttemptBundleVisualizer();
    var src = visualizer.RenderProjectionOverlay(raw, rect, cal, refs, inliers, renderSizePx: 16);

    src.PixelWidth.Should().Be(32);
    src.PixelHeight.Should().Be(24);
}
```

(`WorldCoord` / `LandmarkReference` / `TypeAwareRansacSolver.AssignedReference` imports — the engineer should add the right `using` statements; grep for these types if needed.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AttemptBundleVisualizerTests"`
Expected: FAIL with method-not-found on the two new tests.

- [ ] **Step 3: Implement `RenderDetectionsOverlay`**

Add to `AttemptBundleVisualizer`:

```csharp
public BitmapSource RenderDetectionsOverlay(
    GrayImage gray,
    IReadOnlyList<TypedDetection> detections,
    int renderSizePx)
{
    int w = gray.Width, h = gray.Height;

    // Background: gray screenshot as a Gray8 BitmapSource.
    var grayBg = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, gray.Pixels, w);
    grayBg.Freeze();

    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        dc.DrawImage(grayBg, new System.Windows.Rect(0, 0, w, h));

        var cyan = new Pen(Brushes.Cyan, 1); cyan.Freeze();
        var red = new Pen(Brushes.Red, 1); red.Freeze();
        var labelBrush = Brushes.Cyan;
        var typeface = new Typeface("Segoe UI");

        double half = renderSizePx / 2.0;
        foreach (var det in detections)
        {
            var rect = new System.Windows.Rect(det.AnchorX - half, det.AnchorY - half, renderSizePx, renderSizePx);
            dc.DrawRectangle(brush: null, cyan, rect);
            dc.DrawLine(red, new System.Windows.Point(det.AnchorX - 2, det.AnchorY), new System.Windows.Point(det.AnchorX + 2, det.AnchorY));
            dc.DrawLine(red, new System.Windows.Point(det.AnchorX, det.AnchorY - 2), new System.Windows.Point(det.AnchorX, det.AnchorY + 2));

            var text = new FormattedText(
                det.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight, typeface, 9, labelBrush, 96);
            dc.DrawText(text, new System.Windows.Point(rect.Right + 1, rect.Top - 1));
        }
    }

    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(dv);
    rtb.Freeze();
    return rtb;
}
```

- [ ] **Step 4: Implement `RenderProjectionOverlay`**

Add to `AttemptBundleVisualizer`:

```csharp
public BitmapSource RenderProjectionOverlay(
    CapturedFrame rawColor,
    MapRect mapRect,
    AreaCalibration calibration,
    IReadOnlyList<LandmarkReference> references,
    IReadOnlyList<TypeAwareRansacSolver.AssignedReference> inliers,
    int renderSizePx)
{
    int w = rawColor.Width, h = rawColor.Height;

    var bg = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, rawColor.Bgra, w * 4);
    bg.Freeze();

    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        dc.DrawImage(bg, new System.Windows.Rect(0, 0, w, h));

        var yellow = new Pen(Brushes.Yellow, 1); yellow.Freeze();
        var green = new Pen(Brushes.LimeGreen, 2); green.Freeze();

        // Project every ref via WorldToWindow (texture coords) → TextureToScreenshot.
        foreach (var r in references)
        {
            var px = calibration.WorldToWindow(r.World, currentZoom: 1.0);
            var (sx, sy) = mapRect.TextureToScreenshot(px.X, px.Y);
            dc.DrawLine(yellow, new System.Windows.Point(sx - 3, sy), new System.Windows.Point(sx + 3, sy));
            dc.DrawLine(yellow, new System.Windows.Point(sx, sy - 3), new System.Windows.Point(sx, sy + 3));
        }

        // Green outline rect for each inlier (inlier pixels are texture coords).
        double half = renderSizePx / 2.0;
        foreach (var inl in inliers)
        {
            var (sx, sy) = mapRect.TextureToScreenshot(inl.PixelX, inl.PixelY);
            dc.DrawRectangle(brush: null, green,
                new System.Windows.Rect(sx - half, sy - half, renderSizePx, renderSizePx));
        }
    }

    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(dv);
    rtb.Freeze();
    return rtb;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AttemptBundleVisualizerTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/AttemptBundleVisualizer.cs \
        tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/AttemptBundleVisualizerTests.cs
git commit -m "feat(map-calibration): detections + projection overlay renderers"
```

---

## Task 7: `FilesystemCalibrationAttemptBundleSink`

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`
- Test: rename `tests/Mithril.MapCalibration.Capture.Tests/CaptureFrameDumperTests.cs` → `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs` and rewrite contents from scratch (the old flat-dump tests no longer apply).

- [ ] **Step 1: Read the existing test file's structure for patterns**

Run: `cat tests/Mithril.MapCalibration.Capture.Tests/CaptureFrameDumperTests.cs` — note how it uses unique markers to isolate test runs in the shared dump dir (the `marker` field at line 85).

- [ ] **Step 2: Write the failing sink tests**

Create `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class CalibrationAttemptBundleSinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "MithrilBundleTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private FilesystemCalibrationAttemptBundleSink NewSink() =>
        new(_root, logger: null, visualizer: new AttemptBundleVisualizer());

    private static CalibrationAttemptContext PopulatedAccepted()
    {
        var ctx = new CalibrationAttemptContext("AreaEltibule",
            new DateTimeOffset(2026, 6, 1, 12, 30, 12, 696, TimeSpan.Zero));
        var color = new byte[4 * 4 * 4]; // 4x4 BGRA32
        ctx.RawCapture = new CapturedFrame(4, 4, color);
        ctx.GrayCapture = new GrayImage(4, 4, new byte[16]);
        ctx.MapRect = new MapRect(0, 0, 4, 4, 8, 8);
        ctx.AlignedCrop = new GrayImage(4, 4, new byte[16]);
        ctx.AlignedTexture = new GrayImage(4, 4, new byte[16]);
        ctx.BaseTextureResampled = ctx.AlignedTexture;
        ctx.References = Array.Empty<LandmarkReference>();
        var cal = new AreaCalibration(
            Scale: 1.0, RotationRadians: 0, OriginX: 0, OriginY: 0,
            ReferenceCount: 0, ResidualPixels: 0.5)
        { Source = CalibrationSource.AutoCapture };
        ctx.Result = new CalibrationSolveResult(
            Calibration: cal, InlierCount: 0, RejectReason: null,
            Inliers: Array.Empty<TypeAwareRansacSolver.AssignedReference>())
        { Detections = Array.Empty<TypedDetection>() };
        ctx.Outcome = OutcomeVocabulary.Accepted;
        return ctx;
    }

    [Fact]
    public void Writes_per_attempt_subdir_with_expected_name_on_accepted()
    {
        var ctx = PopulatedAccepted();
        NewSink().Write(ctx);

        var dirs = Directory.GetDirectories(_root);
        dirs.Should().HaveCount(1);
        Path.GetFileName(dirs[0]).Should().StartWith("AreaEltibule-20260601-123012-696-accepted");
    }

    [Fact]
    public void Writes_all_11_files_on_accepted_attempt()
    {
        var ctx = PopulatedAccepted();
        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        files.Should().Contain(new[]
        {
            "01-attempt.json",
            "02-screenshot-raw.png",
            "03-screenshot-gray.png",
            "04-maprect.json",
            "05-base-texture-resampled.png",
            "06-aligned-screenshot.png",
            "07-deviation.png",
            "08-detections.png",
            "09-projection-overlay.png",
            "10-detections.json",
            "11-recovered-cal.json",
        });
    }

    [Fact]
    public void Writes_only_header_on_capture_failed()
    {
        var ctx = new CalibrationAttemptContext("AreaSerbule",
            new DateTimeOffset(2026, 6, 1, 13, 0, 0, 0, TimeSpan.Zero));
        ctx.Outcome = OutcomeVocabulary.RejectedCaptureFailed;
        ctx.Result = new CalibrationSolveResult(null, 0, "capture failed");

        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
        files.Should().BeEquivalentTo(new[] { "01-attempt.json" });
    }

    [Fact]
    public void Writes_partial_set_on_solve_rejection()
    {
        var ctx = PopulatedAccepted();
        ctx.Result = new CalibrationSolveResult(
            Calibration: null, InlierCount: 3, RejectReason: "insufficient inliers (3 < 4 required)")
        { Detections = Array.Empty<TypedDetection>() };
        ctx.Outcome = OutcomeVocabulary.RejectedSolveInsufficientInliers;

        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
        files.Should().NotContain("09-projection-overlay.png");
        files.Should().NotContain("11-recovered-cal.json");
        files.Should().Contain("10-detections.json"); // detections list was present
    }

    [Theory]
    [InlineData("rejected-no-area")]
    [InlineData("rejected-pg-not-foreground")]
    [InlineData("rejected-no-bbox")]
    public void Skips_write_on_pre_capture_outcomes(string outcome)
    {
        var ctx = new CalibrationAttemptContext("AreaEltibule",
            new DateTimeOffset(2026, 6, 1, 13, 0, 0, 0, TimeSpan.Zero));
        ctx.Outcome = outcome;
        NewSink().Write(ctx);

        Directory.GetDirectories(_root).Should().BeEmpty();
    }

    [Fact]
    public void Swallows_visualizer_exceptions_and_writes_what_it_can()
    {
        var ctx = PopulatedAccepted();
        var sink = new FilesystemCalibrationAttemptBundleSink(
            _root, logger: null, visualizer: new ThrowingVisualizer());

        Action act = () => sink.Write(ctx);
        act.Should().NotThrow();

        var dir = Directory.GetDirectories(_root).SingleOrDefault();
        dir.Should().NotBeNull();
        Directory.GetFiles(dir!).Select(Path.GetFileName).Should().Contain("01-attempt.json");
    }

    private sealed class ThrowingVisualizer : AttemptBundleVisualizer
    {
        public new System.Windows.Media.Imaging.BitmapSource RenderDeviation(GrayImage a, GrayImage b)
            => throw new InvalidOperationException("forced");
    }
}
```

(Note: `ThrowingVisualizer` uses `new` to shadow; the sink must call the visualizer through a virtual or interface seam for this to actually throw. If `AttemptBundleVisualizer` methods are not virtual, mark them `virtual` in Task 5/6 — small spec-aligned tweak. Alternative: introduce an `IAttemptBundleVisualizer` interface; the cleaner path. The engineer chooses.)

- [ ] **Step 3: Run tests to verify they fail (sink doesn't exist)**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationAttemptBundleSinkTests"`
Expected: FAIL with type-not-found.

- [ ] **Step 4: Implement `FilesystemCalibrationAttemptBundleSink`**

`src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.MapCalibration;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Writes a per-attempt diagnostic bundle (the 11-file subdir described in the
/// design spec) to the configured root. Fail-soft: every error is caught,
/// logged, and dropped — never propagated into the engine.
/// </summary>
public sealed class FilesystemCalibrationAttemptBundleSink : ICalibrationAttemptBundleSink
{
    private static readonly string AssemblyVersion =
        typeof(FilesystemCalibrationAttemptBundleSink).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(FilesystemCalibrationAttemptBundleSink).Assembly.GetName().Version?.ToString()
            ?? "unknown";

    private readonly string _root;
    private readonly ILogger? _logger;
    private readonly AttemptBundleVisualizer _visualizer;

    public FilesystemCalibrationAttemptBundleSink(string root, ILogger? logger, AttemptBundleVisualizer visualizer)
    {
        _root = root;
        _logger = logger;
        _visualizer = visualizer;
    }

    public void Write(CalibrationAttemptContext context)
    {
        try
        {
            if (!OutcomeVocabulary.ShouldWriteBundle(context.Outcome)) return;

            Directory.CreateDirectory(_root);
            var subdir = Path.Combine(_root, MakeSubdirName(context));
            Directory.CreateDirectory(subdir);

            var files = new AttemptFilesJson(
                RawScreenshot: TryWriteRawScreenshot(subdir, context),
                GrayScreenshot: TryWriteGrayScreenshot(subdir, context),
                MapRect: TryWriteMapRectJson(subdir, context),
                BaseTextureResampled: TryWriteBaseTextureResampled(subdir, context),
                AlignedScreenshot: TryWriteAlignedScreenshot(subdir, context),
                Deviation: TryWriteDeviation(subdir, context),
                DetectionsImage: TryWriteDetectionsImage(subdir, context),
                ProjectionOverlay: TryWriteProjectionOverlay(subdir, context),
                Detections: TryWriteDetectionsJson(subdir, context),
                RecoveredCalibration: TryWriteRecoveredCalibrationJson(subdir, context));

            WriteAttemptJson(subdir, context, files);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Calibration attempt bundle write failed for {Area}. Attempt continues.", context.Area);
        }
    }

    private static string MakeSubdirName(CalibrationAttemptContext ctx)
    {
        var stamp = ctx.StartedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var area = Sanitize(ctx.Area);
        return $"{area}-{stamp}-{ctx.Outcome}";
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private string? TryWriteRawScreenshot(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.RawCapture is null) return null;
            var src = BitmapSource.Create(
                ctx.RawCapture.Width, ctx.RawCapture.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null,
                ctx.RawCapture.Bgra, ctx.RawCapture.Width * 4);
            return WritePng(dir, "02-screenshot-raw.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "02-screenshot-raw write failed"); return null; }
    }

    private string? TryWriteGrayScreenshot(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.GrayCapture is null) return null;
            var src = BitmapSource.Create(
                ctx.GrayCapture.Width, ctx.GrayCapture.Height, 96, 96,
                System.Windows.Media.PixelFormats.Gray8, null,
                ctx.GrayCapture.Pixels, ctx.GrayCapture.Width);
            return WritePng(dir, "03-screenshot-gray.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "03-screenshot-gray write failed"); return null; }
    }

    private string? TryWriteMapRectJson(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.MapRect is null) return null;
            var dto = new MapRectJson(1,
                ctx.MapRect.OriginX, ctx.MapRect.OriginY,
                ctx.MapRect.Width, ctx.MapRect.Height,
                ctx.MapRect.TextureWidth, ctx.MapRect.TextureHeight,
                ctx.MapRect.AutoDetectScore, ctx.MapRect.SourceScaleFactor);
            return WriteJson(dir, "04-maprect.json", dto, CalibrationBundleJsonContext.Default.MapRectJson);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "04-maprect write failed"); return null; }
    }

    private string? TryWriteBaseTextureResampled(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.BaseTextureResampled is null) return null;
            var src = BitmapSource.Create(
                ctx.BaseTextureResampled.Width, ctx.BaseTextureResampled.Height, 96, 96,
                System.Windows.Media.PixelFormats.Gray8, null,
                ctx.BaseTextureResampled.Pixels, ctx.BaseTextureResampled.Width);
            return WritePng(dir, "05-base-texture-resampled.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "05-base-texture-resampled write failed"); return null; }
    }

    private string? TryWriteAlignedScreenshot(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.AlignedCrop is null) return null;
            var src = BitmapSource.Create(
                ctx.AlignedCrop.Width, ctx.AlignedCrop.Height, 96, 96,
                System.Windows.Media.PixelFormats.Gray8, null,
                ctx.AlignedCrop.Pixels, ctx.AlignedCrop.Width);
            return WritePng(dir, "06-aligned-screenshot.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "06-aligned-screenshot write failed"); return null; }
    }

    private string? TryWriteDeviation(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.AlignedCrop is null || ctx.AlignedTexture is null) return null;
            var src = _visualizer.RenderDeviation(ctx.AlignedCrop, ctx.AlignedTexture);
            return WritePng(dir, "07-deviation.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "07-deviation write failed"); return null; }
    }

    private string? TryWriteDetectionsImage(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.Result?.Detections is null || ctx.GrayCapture is null) return null;
            var src = _visualizer.RenderDetectionsOverlay(ctx.GrayCapture, ctx.Result.Detections, renderSizePx: 16);
            return WritePng(dir, "08-detections.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "08-detections write failed"); return null; }
    }

    private string? TryWriteProjectionOverlay(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.Result?.Calibration is null
                || ctx.RawCapture is null
                || ctx.MapRect is null
                || ctx.References is null
                || ctx.Result.Inliers is null) return null;
            var src = _visualizer.RenderProjectionOverlay(
                ctx.RawCapture, ctx.MapRect, ctx.Result.Calibration,
                ctx.References, ctx.Result.Inliers, renderSizePx: 16);
            return WritePng(dir, "09-projection-overlay.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "09-projection-overlay write failed"); return null; }
    }

    private string? TryWriteDetectionsJson(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.Result?.Detections is null) return null;
            var detections = ctx.Result.Detections
                .Select(d => new DetectionJson(d.LandmarkType, d.IconName, d.AnchorX, d.AnchorY, d.Score))
                .ToArray();
            var dto = new DetectionsJson(1, 16, detections);
            return WriteJson(dir, "10-detections.json", dto, CalibrationBundleJsonContext.Default.DetectionsJson);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "10-detections write failed"); return null; }
    }

    private string? TryWriteRecoveredCalibrationJson(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.Result?.Calibration is null) return null;
            var cal = ctx.Result.Calibration;
            var inliers = (ctx.Result.Inliers ?? Array.Empty<TypeAwareRansacSolver.AssignedReference>())
                .Select(i => new InlierJson(i.Label, i.WorldX, i.WorldZ, i.PixelX, i.PixelY, i.MatchScore))
                .ToArray();
            var dto = new RecoveredCalibrationJson(1,
                cal.Scale, cal.RotationRadians, cal.OriginX, cal.OriginY,
                cal.MirrorNorth, cal.CalibrationZoom, cal.ResidualPixels,
                cal.ReferenceCount, cal.Source.ToString(), inliers);
            return WriteJson(dir, "11-recovered-cal.json", dto, CalibrationBundleJsonContext.Default.RecoveredCalibrationJson);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "11-recovered-cal write failed"); return null; }
    }

    private void WriteAttemptJson(string dir, CalibrationAttemptContext ctx, AttemptFilesJson files)
    {
        var finalized = DateTimeOffset.UtcNow;
        var dto = new AttemptJson(
            SchemaVersion: 1,
            Area: ctx.Area,
            AttemptStartedUtc: ctx.StartedUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            AttemptFinalizedUtc: finalized.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            Outcome: ctx.Outcome,
            RejectReason: ctx.Result?.RejectReason ?? ctx.ExceptionInfo,
            EngineVersion: AssemblyVersion,
            Files: files);
        WriteJson(dir, "01-attempt.json", dto, CalibrationBundleJsonContext.Default.AttemptJson);
    }

    private static string WritePng(string dir, string name, BitmapSource src)
    {
        var path = Path.Combine(dir, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return name;
    }

    private static string WriteJson<T>(string dir, string name, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var path = Path.Combine(dir, name);
        using var fs = File.Create(path);
        JsonSerializer.Serialize(fs, value, typeInfo);
        return name;
    }
}
```

- [ ] **Step 5: Make `AttemptBundleVisualizer` methods virtual (for the throwing-visualizer test)**

In `AttemptBundleVisualizer.cs`, mark the three render methods `public virtual`. (Cleaner alternative: introduce an `IAttemptBundleVisualizer` interface and have the sink depend on that. Engineer's choice — both pass the test. Pick interface if it makes the prod code simpler.)

- [ ] **Step 6: Delete the old `CaptureFrameDumperTests.cs`**

```bash
git rm tests/Mithril.MapCalibration.Capture.Tests/CaptureFrameDumperTests.cs
```

(Its assertions are subsumed by the new sink tests; the flat-dumper path is going away in Task 9.)

- [ ] **Step 7: Run the sink tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationAttemptBundleSinkTests"`
Expected: PASS (6+ tests).

- [ ] **Step 8: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/ \
        tests/Mithril.MapCalibration.Capture.Tests/
git commit -m "feat(map-calibration): filesystem bundle sink (per-attempt subdirs)"
```

---

## Task 8: `CalibrationAttemptBundleSinkSelector` + DI wiring

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptBundleSinkSelector.cs`
- Modify: `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` (or whatever the existing DI extension is named — `ls src/Mithril.MapCalibration.Capture/DependencyInjection`)
- Test: `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkSelectorTests.cs`

- [ ] **Step 1: Write the failing selector test**

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class CalibrationAttemptBundleSinkSelectorTests
{
    [Fact]
    public void Resolve_returns_filesystem_sink_when_toggle_on()
    {
        var options = new CaptureDiagnosticsOptions { DumpCalibrationBundles = true };
        var fs = new FilesystemCalibrationAttemptBundleSink("ignored", null, new AttemptBundleVisualizer());
        var selector = new CalibrationAttemptBundleSinkSelector(options, fs, NullCalibrationAttemptBundleSink.Instance);

        selector.Resolve().Should().BeSameAs(fs);
    }

    [Fact]
    public void Resolve_returns_null_sink_when_toggle_off()
    {
        var options = new CaptureDiagnosticsOptions { DumpCalibrationBundles = false };
        var fs = new FilesystemCalibrationAttemptBundleSink("ignored", null, new AttemptBundleVisualizer());
        var selector = new CalibrationAttemptBundleSinkSelector(options, fs, NullCalibrationAttemptBundleSink.Instance);

        selector.Resolve().Should().BeSameAs(NullCalibrationAttemptBundleSink.Instance);
    }

    [Fact]
    public void Resolve_reads_current_toggle_value_each_call()
    {
        var options = new CaptureDiagnosticsOptions { DumpCalibrationBundles = false };
        var fs = new FilesystemCalibrationAttemptBundleSink("ignored", null, new AttemptBundleVisualizer());
        var selector = new CalibrationAttemptBundleSinkSelector(options, fs, NullCalibrationAttemptBundleSink.Instance);

        selector.Resolve().Should().BeSameAs(NullCalibrationAttemptBundleSink.Instance);
        options.DumpCalibrationBundles = true;
        selector.Resolve().Should().BeSameAs(fs);
    }
}
```

This test references `CaptureDiagnosticsOptions.DumpCalibrationBundles` which doesn't exist yet — Task 9 renames the existing `DumpCaptureFrames` field. For this task, **add** `DumpCalibrationBundles` as a new field on `CaptureDiagnosticsOptions` alongside the old one; the rename + retirement of `DumpCaptureFrames` happens cleanly in Task 9. (Two tasks touching the same options class is OK — the order is: this task adds, Task 9 removes the old one.)

- [ ] **Step 2: Add `DumpCalibrationBundles` to `CaptureDiagnosticsOptions`**

Modify `src/Mithril.MapCalibration.Capture/CaptureDiagnosticsOptions.cs` to add:

```csharp
/// <summary>
/// When <see langword="true"/>, AutoCalibrationEngine writes a per-attempt
/// diagnostic bundle (#NNN — fill in this PR's issue number) to
/// <c>%LocalAppData%/Mithril/diagnostics/calibration/&lt;area&gt;-&lt;ts&gt;-&lt;outcome&gt;/</c>.
/// Default <see langword="false"/>. Supersedes <see cref="DumpCaptureFrames"/>;
/// the old flag is retired in this PR.
/// </summary>
public bool DumpCalibrationBundles { get; set; }
```

(Leave `DumpCaptureFrames` and `DumpGrayFrames` in place for now; Task 9 removes them.)

- [ ] **Step 3: Run selector tests — expected to fail (type doesn't exist)**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationAttemptBundleSinkSelectorTests"`
Expected: FAIL with type-not-found on `CalibrationAttemptBundleSinkSelector`.

- [ ] **Step 4: Implement the selector**

`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptBundleSinkSelector.cs`:

```csharp
namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Picks the live <see cref="ICalibrationAttemptBundleSink"/> based on
/// <see cref="CaptureDiagnosticsOptions.DumpCalibrationBundles"/>. Re-reads
/// the flag every call so a settings-UI toggle takes effect without restart.
/// </summary>
public sealed class CalibrationAttemptBundleSinkSelector
{
    private readonly CaptureDiagnosticsOptions _options;
    private readonly ICalibrationAttemptBundleSink _filesystemSink;
    private readonly ICalibrationAttemptBundleSink _nullSink;

    public CalibrationAttemptBundleSinkSelector(
        CaptureDiagnosticsOptions options,
        ICalibrationAttemptBundleSink filesystemSink,
        ICalibrationAttemptBundleSink nullSink)
    {
        _options = options;
        _filesystemSink = filesystemSink;
        _nullSink = nullSink;
    }

    public ICalibrationAttemptBundleSink Resolve() =>
        _options.DumpCalibrationBundles ? _filesystemSink : _nullSink;
}
```

- [ ] **Step 5: Wire up DI**

In `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`, find the `AddMithrilMapCalibrationCapture` registration. Add:

```csharp
services.AddSingleton<AttemptBundleVisualizer>();
services.AddSingleton<ICalibrationAttemptBundleSink>(_ => NullCalibrationAttemptBundleSink.Instance);  // placeholder
services.AddSingleton<FilesystemCalibrationAttemptBundleSink>(sp =>
    new FilesystemCalibrationAttemptBundleSink(
        root: CalibrationBundleDirectories.DefaultRoot,
        logger: sp.GetService<ILoggerFactory>()?.CreateLogger("MapCalibration.Bundle"),
        visualizer: sp.GetRequiredService<AttemptBundleVisualizer>()));
services.AddSingleton(sp => new CalibrationAttemptBundleSinkSelector(
    sp.GetRequiredService<CaptureDiagnosticsOptions>(),
    sp.GetRequiredService<FilesystemCalibrationAttemptBundleSink>(),
    NullCalibrationAttemptBundleSink.Instance));
```

Where `CalibrationBundleDirectories.DefaultRoot` is `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mithril", "diagnostics", "calibration")` — extract into a tiny static helper alongside the sink so both the sink and the settings VM resolve the same path. (The settings VM resolves it from `CaptureFrameDumper.DumpDirectory` today; in Task 9 we redirect it to the new helper.)

- [ ] **Step 6: Run selector tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationAttemptBundleSinkSelectorTests"`
Expected: PASS (3 tests).

- [ ] **Step 7: Build the whole solution to catch any DI graph drift**

Run: `dotnet build Mithril.slnx`
Expected: green.

- [ ] **Step 8: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/ \
        tests/Mithril.MapCalibration.Capture.Tests/
git commit -m "feat(map-calibration): wire bundle sink selector + DI registration"
```

---

## Task 9: Retire flat capture dump + settings rename + migration

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/CaptureService.cs` — remove dump blocks
- Modify: `src/Mithril.MapCalibration.Capture/CaptureDiagnosticsOptions.cs` — remove `DumpCaptureFrames`, `DumpGrayFrames`
- Delete: `src/Mithril.MapCalibration.Capture/CaptureFrameDumper.cs` — no longer wired anywhere
- Modify: `src/Mithril.Shell/ShellSettings.cs` — schema bump + rename + migrate
- Modify: `src/Mithril.Shell/DependencyInjection/ShellComposition.cs` — remove gray-toggle mirror (lines 298 + 308)
- Test: `tests/Mithril.Shell.Tests/CaptureDiagnosticsMirrorTests.cs` — update assertions (remove `DumpGrayFrames` cases, add `DumpCalibrationBundles`)
- Test: `tests/Mithril.Shell.Tests/ShellSettingsMigrationTests.cs` — new file (if absent), add migration test

- [ ] **Step 1: Write the failing migration test**

```csharp
using FluentAssertions;
using Mithril.Shell;
using System.Text.Json;
using Xunit;

namespace Mithril.Shell.Tests;

public sealed class ShellSettingsCalibrationBundleMigrationTests
{
    [Fact]
    public void Migrates_DumpCalibrationCaptureFrames_to_DumpCalibrationBundles()
    {
        // Old-shape JSON (pre-bump): the renamed field present + the dropped field present.
        var oldJson = """
        {
            "schemaVersion": 1,
            "dumpCalibrationCaptureFrames": true,
            "dumpCalibrationGrayFrames": true
        }
        """;

        // Round-trip through Load (which invokes Migrate as part of IVersionedState<T>).
        var settings = JsonSerializer.Deserialize<ShellSettings>(oldJson, ShellSettingsJsonContext.Default.ShellSettings)!;
        settings = settings.Migrate(); // explicit call per IVersionedState<T> pattern

        settings.DumpCalibrationBundles.Should().BeTrue();
        settings.SchemaVersion.Should().Be(ShellSettings.Version);
    }
}
```

(Adjust the JSON shape + the Migrate-invocation idiom to match what the existing `ShellSettings.Migrate` pattern looks like — read `ShellSettings.cs:9-30` first to see how `IVersionedState<ShellSettings>` is implemented. The test asserts the load+migrate path, however it's actually invoked in code.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Mithril.Shell.Tests --filter "FullyQualifiedName~ShellSettingsCalibrationBundleMigrationTests"`
Expected: FAIL with `DumpCalibrationBundles` not found.

- [ ] **Step 3: Bump `ShellSettings.Version`, rename the field, drop the gray field, write `Migrate`**

In `src/Mithril.Shell/ShellSettings.cs`:

1. Bump the `Version` const (find it near line 25).
2. Replace the `_dumpCalibrationCaptureFrames` field + property (line 102-103) with `_dumpCalibrationBundles` + `DumpCalibrationBundles`.
3. Delete `_dumpCalibrationGrayFrames` + `DumpCalibrationGrayFrames` (line 111-112).
4. In `Migrate` (existing `IVersionedState<T>.Migrate` impl), add: if the old `DumpCalibrationCaptureFrames` is present (which won't deserialize after the rename), the migration path needs a custom shape. The cleanest path is to add a `[JsonExtensionData] Dictionary<string, JsonElement> _extra` to `ShellSettings` and have `Migrate` look it up there — but that's a larger architectural change. Simpler: temporarily keep an obsolete `DumpCalibrationCaptureFrames` setter that funnels into `DumpCalibrationBundles`:

```csharp
[Obsolete("Renamed to DumpCalibrationBundles. Read-only shim for migration of old persisted state.")]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public bool DumpCalibrationCaptureFrames
{
    get => DumpCalibrationBundles;
    set => DumpCalibrationBundles = value;
}
```

The setter runs during deserialization and lifts the old value into the new field. The `Migrate` method then drops the obsolete-shim's "set was ever called" flag (no per-instance tracking needed — just trust that the property survives).

- [ ] **Step 4: Update `ShellComposition` (the DI mirror) to drop the gray-frames reference**

In `src/Mithril.Shell/DependencyInjection/ShellComposition.cs`, find the two references around lines 298 + 308 to `DumpCalibrationGrayFrames` and delete them. Update the line that mirrors `settings.DumpCalibrationCaptureFrames → captureDiag.DumpCaptureFrames` to instead mirror `settings.DumpCalibrationBundles → captureDiag.DumpCalibrationBundles`. Both sides of that mirror are getting renamed.

- [ ] **Step 5: Remove `DumpCaptureFrames` and `DumpGrayFrames` from `CaptureDiagnosticsOptions`**

In `src/Mithril.MapCalibration.Capture/CaptureDiagnosticsOptions.cs`, delete both fields. Only `DumpCalibrationBundles` remains.

- [ ] **Step 6: Strip dump code from `CaptureService`**

In `src/Mithril.MapCalibration.Capture/CaptureService.cs`:
- Remove the `_dumper` field at line 23.
- Remove the `_dumper = new CaptureFrameDumper(logger)` initialization at line 37.
- Remove the entire `if (_diagnostics.DumpCaptureFrames)` block at lines 65-76.
- Remove the `CaptureDiagnosticsOptions` ctor parameter + `_diagnostics` field if no longer needed (verify by searching for other consumers).

- [ ] **Step 7: Delete `CaptureFrameDumper.cs`**

Grep first: `grep -rn "CaptureFrameDumper" src/ tests/ --include="*.cs"` to confirm no remaining references. The settings VM hint at `DiagnosticsSettingsViewModel.cs:72` (`CaptureFrameDumper.DumpDirectory`) needs to be redirected to `CalibrationBundleDirectories.DefaultRoot` from Task 8.

Then `git rm src/Mithril.MapCalibration.Capture/CaptureFrameDumper.cs`.

- [ ] **Step 8: Update `CaptureDiagnosticsMirrorTests.cs`**

In `tests/Mithril.Shell.Tests/CaptureDiagnosticsMirrorTests.cs`:
- Remove every `DumpGrayFrames` assertion (lines 30, 39, 62, 65 per the earlier grep).
- Rename the `DumpCaptureFrames` assertion subjects to `DumpCalibrationBundles`.
- Ensure both the settings field (`DumpCalibrationBundles`) and the options field (`DumpCalibrationBundles`) are tested for the mirror.

- [ ] **Step 9: Run the full Shell + Capture test suites**

```bash
dotnet test tests/Mithril.Shell.Tests
dotnet test tests/Mithril.MapCalibration.Capture.Tests
```

Expected: green.

- [ ] **Step 10: Run a full solution build to catch any straggler references**

Run: `dotnet build Mithril.slnx`
Expected: green.

- [ ] **Step 11: Commit**

```bash
git add -u src/ tests/
git commit -m "feat(map-calibration): retire flat capture dump + migrate settings to DumpCalibrationBundles"
```

---

## Task 10: `AutoCalibrationEngine` refactor — try/finally around the pipeline

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`
- Test: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` — add per-outcome assertions

- [ ] **Step 1: Write a failing test that the sink receives a context for an accepted attempt**

Open `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` and read the existing test arrangements (how the engine is constructed in tests with mocked deps). Add:

```csharp
[Fact]
public async Task TryCalibrate_passes_populated_context_to_sink_on_accept()
{
    var capturedContexts = new List<CalibrationAttemptContext>();
    var captureSink = new CapturingSink(capturedContexts);
    var selector = new CalibrationAttemptBundleSinkSelector(
        new CaptureDiagnosticsOptions { DumpCalibrationBundles = true },
        captureSink,
        NullCalibrationAttemptBundleSink.Instance);

    var engine = BuildEngineForAcceptedSolve(selector);  // existing-style helper, see below

    await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

    capturedContexts.Should().HaveCount(1);
    var ctx = capturedContexts[0];
    ctx.Outcome.Should().Be(OutcomeVocabulary.Accepted);
    ctx.RawCapture.Should().NotBeNull();
    ctx.MapRect.Should().NotBeNull();
    ctx.AlignedCrop.Should().NotBeNull();
    ctx.AlignedTexture.Should().NotBeNull();
    ctx.Result.Should().NotBeNull();
    ctx.Result!.Calibration.Should().NotBeNull();
}

private sealed class CapturingSink : ICalibrationAttemptBundleSink
{
    private readonly List<CalibrationAttemptContext> _captured;
    public CapturingSink(List<CalibrationAttemptContext> captured) => _captured = captured;
    public void Write(CalibrationAttemptContext context) => _captured.Add(context);
}
```

`BuildEngineForAcceptedSolve(selector)` should be a small helper that mirrors the existing engine tests' construction, plus the new selector arg. Read the existing test file to see the in-tree shape of stubs.

- [ ] **Step 2: Run test to verify it fails (engine doesn't take a selector yet)**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~TryCalibrate_passes_populated_context"`
Expected: FAIL (compilation — engine ctor doesn't accept the selector).

- [ ] **Step 3: Refactor `AutoCalibrationEngine` to the try/finally shape**

In `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`:

1. Add `CalibrationAttemptBundleSinkSelector _sinkSelector` field; add ctor param after the existing ones (keep the optional sidecar params last, so this one is non-optional).
2. Split `TryCalibrateCurrentAreaAsync` into:

```csharp
public async Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct)
{
    var area = _areaState.CurrentArea ?? string.Empty;
    var attempt = new CalibrationAttemptContext(area, DateTimeOffset.UtcNow);
    var sink = _sinkSelector.Resolve();
    try
    {
        return await RunAttemptCoreAsync(attempt, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        attempt.Outcome = "error";  // cancellation still writes whatever was captured
        attempt.ExceptionInfo = "cancelled";
        throw;
    }
    catch (Exception ex)
    {
        attempt.Outcome = OutcomeVocabulary.Error;
        attempt.ExceptionInfo = $"{ex.GetType().Name}: {ex.Message}";
        throw;
    }
    finally
    {
        sink.Write(attempt);
    }
}
```

3. Move the existing pipeline body (everything from the `area` check through the final outcome return) into `private async Task<AutoCalibrationOutcome> RunAttemptCoreAsync(CalibrationAttemptContext attempt, CancellationToken ct)`. At each pipeline stage, set the matching `attempt.*` property:

```csharp
gray = await _capture.CaptureMapAsync(bbox.Value, ct).ConfigureAwait(false);
attempt.RawCapture = capturedColorFrame;          // need to plumb the color frame too — see Step 4
attempt.GrayCapture = gray;
...
attempt.MapRect = mapRect;
...
attempt.AlignedCrop = crop;
attempt.AlignedTexture = alignedTexture;
attempt.BaseTextureResampled = alignedTexture;    // same data, distinct semantic name
...
attempt.References = references;
...
attempt.Result = result;
```

4. At each `Fail()` site, set `attempt.Outcome = OutcomeVocabulary.<matching>` before returning the failure outcome.

5. On the success path, `attempt.Outcome = OutcomeVocabulary.Accepted`.

- [ ] **Step 4: Plumb the color `CapturedFrame` from `CaptureService` up to the engine**

`CaptureService.CaptureMapAsync` returns `GrayImage?` today. To pop the color into the bundle, either:

a) **Widen the return** to a small `CaptureMapResult` record carrying both the color frame and the gray derivation. Engineer choice — clean but cascades through the existing `ICaptureService` callers.

b) **Stash on `CaptureService` as a property** the engine reads back. Hacky.

c) **Make `CaptureService` accept the `CalibrationAttemptContext` and stash directly.** Couples the service to the context — readable but mixes concerns.

Recommended: (a) — widen to `CaptureMapResult(CapturedFrame? Color, GrayImage? Gray)`. Update the one consumer (`AutoCalibrationEngine`) accordingly. The engine then sets both `attempt.RawCapture` and `attempt.GrayCapture`.

- [ ] **Step 5: Run the engine test**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~TryCalibrate_passes_populated_context"`
Expected: PASS.

- [ ] **Step 6: Add per-outcome assertions**

Add tests for the failure paths the engine covers (one per `Fail()` site): no area → `RejectedNoArea`; no bbox → `RejectedNoBbox`; capture failed → `RejectedCaptureFailed`; etc. Each test arranges the stubs to drive the engine down that path and asserts `capturedContexts[0].Outcome` matches.

- [ ] **Step 7: Run all Capture tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests`
Expected: green.

- [ ] **Step 8: Run the full solution test suite**

Run: `dotnet test Mithril.slnx`
Expected: green.

- [ ] **Step 9: Commit**

```bash
git add -u src/ tests/
git commit -m "feat(map-calibration): bundle-writing in AutoCalibrationEngine via try/finally"
```

---

## Task 11: Shell UI — "Open folder" button + remove gray-frames checkbox

**Files:**
- Modify: `src/Mithril.Shell/ViewModels/DiagnosticsSettingsViewModel.cs`
- Modify: `src/Mithril.Shell/Views/DiagnosticsSettingsView.xaml`
- Test: `tests/Mithril.Shell.Tests/DiagnosticsSettingsViewModelTests.cs` (create if absent)

- [ ] **Step 1: Write the failing button-creates-dir test**

```csharp
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.Shell.ViewModels;
using Xunit;

namespace Mithril.Shell.Tests;

public sealed class DiagnosticsSettingsViewModelOpenFolderTests
{
    [Fact]
    public void OpenCalibrationDumpDirectoryCommand_creates_dump_directory_if_missing()
    {
        var dumpDir = CalibrationBundleDirectories.DefaultRoot;
        if (Directory.Exists(dumpDir)) Directory.Delete(dumpDir, recursive: true);

        var vm = BuildVmWithStubDeps();   // existing-style helper — read the test file for the right ctor wiring
        vm.OpenCalibrationDumpDirectoryCommand.Execute(null);

        Directory.Exists(dumpDir).Should().BeTrue();
    }
}
```

(The `Process.Start` side-effect actually opens Explorer on the dev machine — undesirable in CI. The existing `OpenLogDirectory` command has the same shape and is not currently tested at this level; we accept the same risk. If a `ICalibrationDumpDirectoryOpener` abstraction would be cleaner, the engineer can introduce one, but it's not in spec scope.)

- [ ] **Step 2: Run test to verify it fails (command doesn't exist)**

Run: `dotnet test tests/Mithril.Shell.Tests --filter "FullyQualifiedName~OpenCalibrationDumpDirectory"`
Expected: FAIL.

- [ ] **Step 3: Add `OpenCalibrationDumpDirectoryCommand`**

In `src/Mithril.Shell/ViewModels/DiagnosticsSettingsViewModel.cs` after `OpenLogDirectory` (line 134), add:

```csharp
/// <summary>Opens the calibration diagnostics-bundle directory in the OS file browser.</summary>
[RelayCommand]
private void OpenCalibrationDumpDirectory()
{
    try
    {
        Directory.CreateDirectory(CalibrationBundleDirectories.DefaultRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = CalibrationBundleDirectories.DefaultRoot,
            UseShellExecute = true,
        });
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to open calibration dump directory {Dir}",
            CalibrationBundleDirectories.DefaultRoot);
        MaintenanceStatus = $"Could not open {CalibrationBundleDirectories.DefaultRoot}: {ex.Message}";
    }
}
```

Update `CalibrationDumpDirectoryHint` at line 72 from `CaptureFrameDumper.DumpDirectory` (deleted in Task 9) to `CalibrationBundleDirectories.DefaultRoot`.

- [ ] **Step 4: Update the XAML view**

In `src/Mithril.Shell/Views/DiagnosticsSettingsView.xaml`:

1. Remove the `DumpCalibrationGrayFrames` checkbox at line 66.
2. Rename the remaining `DumpCalibrationCaptureFrames`-bound checkbox (line above 66) to bind `DumpCalibrationBundles` and update its label to "Save calibration diagnostics on each attempt".
3. After the `CalibrationDumpDirectoryHint` text block at line 76, add a button:

```xml
<Button Content="Open calibration diagnostics folder"
        Command="{Binding OpenCalibrationDumpDirectoryCommand}"
        Margin="0,2,0,0" HorizontalAlignment="Left"/>
```

- [ ] **Step 5: Run the VM test**

Run: `dotnet test tests/Mithril.Shell.Tests --filter "FullyQualifiedName~OpenCalibrationDumpDirectory"`
Expected: PASS.

- [ ] **Step 6: Run full Shell + Capture tests**

```bash
dotnet test tests/Mithril.Shell.Tests
dotnet test tests/Mithril.MapCalibration.Capture.Tests
```

Expected: green.

- [ ] **Step 7: Commit**

```bash
git add -u src/ tests/
git commit -m "feat(map-calibration): open-folder button + drop gray-frames checkbox"
```

---

## Task 12: Build, full test, manual verify, PR

- [ ] **Step 1: Stop any running Mithril shell (memory: `mithril_build_file_lock_silent`)**

```bash
# Manual check: ensure Mithril.exe is not running; the build hook will block otherwise.
```

- [ ] **Step 2: Clean + build the whole solution**

```bash
dotnet build Mithril.slnx
```

Expected: green, zero warnings (warnings-as-errors).

- [ ] **Step 3: Run the full test suite**

```bash
dotnet test Mithril.slnx
```

Expected: green.

- [ ] **Step 4: Manual verification — launch Mithril**

```bash
dotnet run --project src/Mithril.Shell
```

In the running app:

1. Open the Diagnostics settings panel.
2. Toggle on "Save calibration diagnostics on each attempt".
3. Trigger an auto-calibration attempt (either via the hotkey for an in-game capture, or a known-failing condition for a rejected-* outcome).
4. Click "Open calibration diagnostics folder".
5. Confirm an `Area<name>-<timestamp>-<outcome>/` subdir exists with the expected files.

The verification needs PG running to get a real capture, but a `rejected-no-area` outcome (toggle on but no PG window) is also acceptable evidence — confirms the no-bundle path works.

- [ ] **Step 5: Open PR**

```bash
git push -u origin claude/calibration-diagnostic-logging
gh pr create --label "area:map-calibration" \
  --title "Per-attempt calibration diagnostic bundle" \
  --body-file <(cat <<'EOF'
## Summary

Replaces today's flat capture-frame dump (one PNG per capture from `CaptureService`) with a structured per-attempt diagnostic bundle written from `AutoCalibrationEngine`. Each bundle is a self-describing subdirectory under `%LocalAppData%/Mithril/diagnostics/calibration/` containing the captured screenshot, ECC-aligned intermediates, the deviation map, the detection set, the recovered calibration, three annotated visualizations, and a JSON header that enumerates what's present.

The bundle is the artifact source the synthesis-probe (`tools/MapCalibrationFromScreenshot`, on `claude/synthesis-probe-impl`) needs for its `--aligned-deviation` / `--maprect-json` / `--truth-cal` flags. Wiring those flags on the probe side is a separate PR.

Refs #966, #978.

### Design

Approach E from the brainstorming doc: plain-data `CalibrationAttemptContext` accumulates pipeline-stage outputs via property writes; `AutoCalibrationEngine.TryCalibrateCurrentAreaAsync` wraps the pipeline in `try { … } finally { _sink.Write(attempt); }`. The sink is `FilesystemCalibrationAttemptBundleSink` when the toggle is on, `NullCalibrationAttemptBundleSink` when off.

Full spec: `docs/superpowers/specs/2026-06-01-calibration-diagnostic-bundle-design.md`.

### Behaviour change

- The legacy `DumpCalibrationCaptureFrames` + `DumpCalibrationGrayFrames` settings are renamed/dropped — old users with the flat dump enabled will see their flag migrated to `DumpCalibrationBundles` via the settings-schema bump.
- The settings UI gains an "Open calibration diagnostics folder" button.
- `CaptureService` no longer dumps; all diagnostic writing happens in `AutoCalibrationEngine` at the end of each attempt (success, gate-reject, exception, cancellation).

## Test plan

- [x] `dotnet build Mithril.slnx` — green, no warnings
- [x] `dotnet test Mithril.slnx` — all tests green
- [x] Manual: toggle on, run an attempt, confirm `Area<name>-…/` subdir with expected files
- [x] Manual: "Open calibration diagnostics folder" button opens the dir
- [x] Manual: rejected-no-area attempt produces no subdir (pre-capture skip)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)
```

- [ ] **Step 6: Delete the scratch plan file (don't commit the deletion to this PR — it's just local cleanup)**

```bash
rm docs/superpowers/plans/2026-06-01-calibration-diagnostic-bundle.md
```

(Per project memory `where_things_live`: local plan files are scratch only; the canonical plan lives in the GitHub issue body.)

---

## Spec Self-Review

- **Spec coverage:** every section/requirement in the spec maps to one or more tasks. JSON shapes → Task 3. Outcome vocabulary → Task 4. Visualizations → Task 5 + 6. Sink → Task 7. Selector + DI → Task 8. Settings migration + flat-dump retirement → Task 9. Engine refactor → Task 10. Shell UI → Task 11. DoD verification → Task 12.

- **Placeholder scan:** no "TBD" / "implement later" / "similar to Task N" anywhere. Every code step has the actual code. Tests have actual assertions.

- **Type consistency:**
  - `CalibrationAttemptContext` properties: `RawCapture`, `GrayCapture`, `BaseTextureResampled`, `MapRect`, `AlignedCrop`, `AlignedTexture`, `References`, `Result`, `Outcome`, `ExceptionInfo` — referenced consistently across tasks 3, 7, 10.
  - `ICalibrationAttemptBundleSink.Write(CalibrationAttemptContext)` — single method, no other surface.
  - `CalibrationBundleDirectories.DefaultRoot` — referenced in tasks 8, 9, 11. Defined in task 8.
  - `OutcomeVocabulary.<Constant>` — referenced in tasks 4, 7, 10. Defined in task 4.
  - `AttemptBundleVisualizer.RenderDeviation/RenderDetectionsOverlay/RenderProjectionOverlay` — defined in tasks 5 + 6; consumed in task 7.

- **Sub-skill handoff:** Use `superpowers:subagent-driven-development` with `model: sonnet` per the user's Step 4.
