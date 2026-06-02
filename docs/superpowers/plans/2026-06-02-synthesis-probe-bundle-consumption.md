# Synthesis-Probe Bundle Consumption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the synthesis-probe diagnostic (`tools/MapCalibrationFromScreenshot`) to consume the per-attempt calibration diagnostic bundles produced by Mithril's live engine after PR #985. Specifically: read `04-maprect.json` for the texture↔aligned-pair transform, `11-recovered-cal.json` for the production-recovered truth `AreaCalibration`, and `07-deviation.png` for the post-ECC deviation map; derive a screenshot-crop-space `CandidateTransform` for truth-cal; and tighten E5's scale bracket using the MapRect's resize ratio so the tiny-scale degeneracy is excluded by construction.

**Architecture:** Six additive CLI flags (`--bundle-dir`, `--maprect-json`, `--recovered-cal-json`, `--aligned-deviation`, `--detections-json`, `--hand-truth-cal`); a `BundleLoader` that resolves a directory into the typed JSON DTOs (replicas of `Mithril.MapCalibration.Capture.Diagnostics.CalibrationBundleJson` — the tool stays decoupled from the WPF-flavored Capture project); a `CandidateTransform.FromRecoveredCalibration(RecoveredCalibrationJson, MapRect)` converter that handles texture-pixel → aligned-pair-pixel conversion; an `IconLikelihoodField.LoadDeviationAsField` alternate to `Build` that skips the screenshot-minus-base subtraction when the live engine has already produced the deviation; and an E5 scale-bracket function that narrows the search to `±20%` around the MapRect-implied scale. The existing `--screenshot`/`--map-rect`/`--truth-cal` paths remain — bundle consumption is opt-in.

**Truth-cal precedence in `SynthesisProbePhase`** (highest priority first):

1. `--truth-cal scale,rot,ox,oy,mirror` — explicit, **crop-pixel space** (used for synthetic tests and legacy probe runs). Wins outright.
2. `--hand-truth-cal scale,rot,ox,oy,mirror` — explicit, **texture-pixel space**. Converted to crop-pixel via `MapRectConversion.FromRecoveredCalibration` using `--maprect-json` (or the MapRect resolved from `--bundle-dir`). Used when production's `--recovered-cal-json` is known-wrong (e.g. 2026-06-02 captures: the live engine accepts 4-inlier fits at residual ≈4 px that are geometrically wrong, so the user supplies the hand-verified texture-space cal — typically the `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json` entry for the area).
3. `--recovered-cal-json` (auto from `--bundle-dir` or explicit) + `--maprect-json` — converted via the same path as `--hand-truth-cal`. Used when production's recovered cal is trustworthy.
4. Error: no truth-cal source → exit 2.

**Tech Stack:** .NET 10 / net10.0-windows, xunit + FluentAssertions, source-generated JSON contexts (System.Text.Json), existing tool primitives (`CandidateTransform`, `JEvaluator`, `IconLikelihoodField`, `SynthesisProbeWriter`, `SynthesisProbeTracer`).

**Context:** This plan executes on `claude/synthesis-probe-impl` (already rebased onto merged main at `d0d7f192`, 29/29 tests passing). Spec: [`docs/superpowers/specs/2026-06-01-synthesis-probe-diagnostic-design.md`](../specs/2026-06-01-synthesis-probe-diagnostic-design.md) plus its "Results & open questions" section that motivated the bundle work. PR #985's design spec: [`docs/superpowers/specs/2026-06-01-calibration-diagnostic-bundle-design.md`](../specs/2026-06-01-calibration-diagnostic-bundle-design.md) (now on `main`).

**Task blocking:** Three task blocks, each one subagent dispatch:

- **Task 1** — Bundle data layer (DTOs + Converter + Loader). All in new `SynthesisProbe/Bundle/` namespace.
- **Task 2** — Probe consumers + CLI surface (alternate `IconLikelihoodField` entry point + five new CLI flags).
- **Task 3** — Integration + E5 bracket + README (the wiring that ties it all together, plus the tightened scale bracket, plus the doc update).

---

## File Structure

### New files (tool project, `tools/MapCalibrationFromScreenshot/`)

| File | Responsibility |
|---|---|
| `SynthesisProbe/Bundle/BundleJsonDtos.cs` | Replicates the bundle's JSON DTOs (`AttemptJson`, `AttemptFilesJson`, `MapRectJson`, `RecoveredCalibrationJson`, `InlierJson`, `DetectionsJson`, `DetectionJson`) with a source-generated `JsonSerializerContext`. The tool needs to deserialize bundle artifacts but doesn't take a project reference on `Mithril.MapCalibration.Capture` (which is WPF-flavored). Shape parity is enforced by a round-trip test. |
| `SynthesisProbe/Bundle/LoadedBundle.cs` | Plain record carrying `AttemptJson Attempt`, `MapRectJson? MapRect`, `RecoveredCalibrationJson? RecoveredCal`, `DetectionsJson? Detections`, `string? DeviationPath`. Optional fields are null when the corresponding bundle file isn't present (e.g. `RecoveredCalibration` is null on rejected attempts). |
| `SynthesisProbe/Bundle/BundleLoader.cs` | Resolves a bundle directory: reads `01-attempt.json` to discover file names, then deserializes each typed JSON + opens the deviation PNG. Exposes `BundleLoader.Open(string dir) → LoadedBundle`. |
| `SynthesisProbe/Bundle/MapRectConversion.cs` | Holds `CandidateTransform FromRecoveredCalibration(RecoveredCalibrationJson cal, MapRect mapRect, out double anisotropyPercent)` (+ overload without the out-param). Translates a production-recovered `AreaCalibration` (texture-pixel space) to the aligned-pair-pixel space the synthesis probe scores in. |

### Modified files (tool project, `tools/MapCalibrationFromScreenshot/`)

- `CliArgs.cs` — add `BundleDir`, `MapRectJsonPath`, `RecoveredCalJsonPath`, `AlignedDeviationPath`, `DetectionsJsonPath`, and `HandTruthCal` fields, parsers, help text.
- `SynthesisProbe/IconLikelihoodField.cs` — add `LoadDeviationAsField(GrayImage deviation, IconTemplate template)` static method that skips the screenshot-minus-base step and runs `ScoreAll` directly on the supplied deviation.
- `SynthesisProbe/SynthesisProbePhase.cs` — branch on `--bundle-dir` / `--aligned-deviation`: if a deviation is provided directly, skip the auto-load + screenshot-minus-base step. If `--bundle-dir` and `--recovered-cal-json` are both present, derive truth-cal automatically. The legacy `--screenshot` + `--map-rect` + `--truth-cal` path stays for synthetic tests.
- `SynthesisProbe/Experiments/E5_ColdGrid.cs` — narrow `scaleBracket` from `[0.1, 2.0]` to `±20%` around an expected scale via a new `BracketAroundExpected` helper; pass that bracket through from `SynthesisProbePhase` when a MapRect is available.
- `README.md` — document the bundle-driven workflow.

### New files (test project, `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/`)

| File | Tests |
|---|---|
| `Bundle/BundleJsonDtosTests.cs` | Round-trip canonical bundle JSON strings through `BundleJsonContext`: `MapRectJson`, `RecoveredCalibrationJson` (with inliers), `AttemptJson`. |
| `Bundle/MapRectConversionTests.cs` | `FromRecoveredCalibration` round-trip: known `(scale_texture, origin_texture)` + `MapRect` produces a `CandidateTransform` whose `Apply(WorldCoord)` matches `MapRect.TextureToScreenshot(AreaCalibration.WorldToWindow(world)) − MapRect.Origin` for a test point. Anisotropic-MapRect warning path. |
| `Bundle/BundleLoaderTests.cs` | Open a synthetic bundle directory built in temp during the test, confirm typed values + nullability (rejected attempts have null `RecoveredCal`). Verify `Throws<FileNotFoundException>` when `01-attempt.json` is absent. |
| `IconLikelihoodFieldLoadDeviationTests.cs` | `LoadDeviationAsField` on a known synthetic deviation (one bright cross at (32, 32) on a 64×64 black background) peaks at (32, 32) with score > 0.8. |
| `CliArgsBundleFlagsTests.cs` | Six facts, one per new flag, asserting `CliArgs.Parse` puts the value on the right record field. Includes `--hand-truth-cal scale,rot,ox,oy,mirror` parsing into a `(double, double, double, double, bool)?` tuple — same shape as the existing `--truth-cal` parser. |
| `Experiments/E5_ColdGrid_BracketedTests.cs` | `BracketAroundExpected(0.5, 0.2)` → all explored scales within `[0.4, 0.6]`. |

---

### Task 1: Bundle data layer (DTOs + Loader + Converter)

This block builds the entire `SynthesisProbe/Bundle/` namespace as one unit — the DTOs, the loader, and the conversion helper. They're tightly coupled (loader and converter both consume the DTOs) so one subagent should hold the whole bundle data layer in context.

**Files:**

- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/BundleJsonDtos.cs`
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/LoadedBundle.cs`
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/BundleLoader.cs`
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/MapRectConversion.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Bundle/BundleJsonDtosTests.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Bundle/MapRectConversionTests.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Bundle/BundleLoaderTests.cs`

#### Sub-task 1A — DTOs

- [ ] **1A.1: Write the failing test**

```csharp
// tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Bundle/BundleJsonDtosTests.cs
using System.Text.Json;
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Bundle;

public class BundleJsonDtosTests
{
    [Fact]
    public void MapRectJson_round_trips()
    {
        const string json = """
            { "schemaVersion": 1,
              "originX": 130, "originY": 60,
              "width": 995, "height": 986,
              "textureWidth": 2048, "textureHeight": 2033,
              "autoDetectScore": null, "sourceScaleFactor": null }
            """;

        var parsed = JsonSerializer.Deserialize(json, BundleJsonContext.Default.MapRectJson)!;

        parsed.SchemaVersion.Should().Be(1);
        parsed.OriginX.Should().Be(130);
        parsed.OriginY.Should().Be(60);
        parsed.Width.Should().Be(995);
        parsed.Height.Should().Be(986);
        parsed.TextureWidth.Should().Be(2048);
        parsed.TextureHeight.Should().Be(2033);
        parsed.AutoDetectScore.Should().BeNull();
        parsed.SourceScaleFactor.Should().BeNull();
    }

    [Fact]
    public void RecoveredCalibrationJson_round_trips_with_inliers()
    {
        const string json = """
            { "schemaVersion": 1,
              "scale": 0.31536, "rotationRadians": -3.14153,
              "originX": 1039.45, "originY": -36.38,
              "mirrorNorth": false, "calibrationZoom": 1.0,
              "residualPixels": 0.34, "referenceCount": 4,
              "source": "UserRefinement",
              "inliers": [
                { "label": "Meditation Pillar", "worldX": 916.8, "worldZ": 2428.8,
                  "pixelX": 179.8, "pixelY": 235.6, "matchScore": 0.921 }
              ] }
            """;

        var parsed = JsonSerializer.Deserialize(json, BundleJsonContext.Default.RecoveredCalibrationJson)!;

        parsed.Scale.Should().BeApproximately(0.31536, 1e-9);
        parsed.MirrorNorth.Should().BeFalse();
        parsed.Inliers.Should().HaveCount(1);
        parsed.Inliers[0].Label.Should().Be("Meditation Pillar");
    }

    [Fact]
    public void AttemptJson_round_trips()
    {
        const string json = """
            { "schemaVersion": 1,
              "area": "AreaEltibule",
              "attemptStartedUtc": "2026-06-02T01:23:45Z",
              "attemptFinalizedUtc": "2026-06-02T01:23:46Z",
              "outcome": "accepted",
              "rejectReason": null,
              "engineVersion": "1.0.0",
              "files": {
                "rawScreenshot": "02-screenshot-raw.png",
                "grayScreenshot": "03-screenshot-gray.png",
                "mapRect": "04-maprect.json",
                "baseTextureResampled": "05-base-resampled.png",
                "alignedScreenshot": "06-aligned-screenshot.png",
                "deviation": "07-deviation.png",
                "detectionsImage": "08-detections.png",
                "projectionOverlay": "09-projection-overlay.png",
                "detections": "10-detections.json",
                "recoveredCalibration": "11-recovered-cal.json"
              } }
            """;

        var parsed = JsonSerializer.Deserialize(json, BundleJsonContext.Default.AttemptJson)!;

        parsed.Area.Should().Be("AreaEltibule");
        parsed.Outcome.Should().Be("accepted");
        parsed.RejectReason.Should().BeNull();
        parsed.Files.Deviation.Should().Be("07-deviation.png");
        parsed.Files.RecoveredCalibration.Should().Be("11-recovered-cal.json");
    }
}
```

- [ ] **1A.2: Run, verify it fails**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~BundleJsonDtosTests"
```

Expected: BUILD FAILURE — `Bundle.BundleJsonContext` not found.

- [ ] **1A.3: Implement the DTOs + context**

```csharp
// tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/BundleJsonDtos.cs
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

// Mirrors src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs.
// Replicated here (rather than ProjectReference'd) to keep the tool decoupled
// from the WPF-flavored Capture project. Shape parity is the contract — if
// CalibrationBundleJsonContext gains a field, mirror it here too.
internal sealed record AttemptJson(
    int SchemaVersion,
    string Area,
    string AttemptStartedUtc,
    string AttemptFinalizedUtc,
    string Outcome,
    string? RejectReason,
    string EngineVersion,
    AttemptFilesJson Files);

internal sealed record AttemptFilesJson(
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

internal sealed record MapRectJson(
    int SchemaVersion,
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight,
    double? AutoDetectScore,
    double? SourceScaleFactor);

internal sealed record DetectionJson(
    string LandmarkType,
    string IconName,
    double AnchorX,
    double AnchorY,
    double Score);

internal sealed record DetectionsJson(
    int SchemaVersion,
    int RenderSizePx,
    IReadOnlyList<DetectionJson> Detections);

internal sealed record InlierJson(
    string Label,
    double WorldX,
    double WorldZ,
    double PixelX,
    double PixelY,
    double MatchScore);

internal sealed record RecoveredCalibrationJson(
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
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AttemptJson))]
[JsonSerializable(typeof(MapRectJson))]
[JsonSerializable(typeof(DetectionsJson))]
[JsonSerializable(typeof(RecoveredCalibrationJson))]
internal partial class BundleJsonContext : JsonSerializerContext;
```

- [ ] **1A.4: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~BundleJsonDtosTests"
```

Expected: PASS — 3 tests.

#### Sub-task 1B — MapRectConversion

- [ ] **1B.1: Write the failing test**

```csharp
// tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Bundle/MapRectConversionTests.cs
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Bundle;

public class MapRectConversionTests
{
    [Fact]
    public void FromRecoveredCalibration_projects_world_to_aligned_pair_pixel()
    {
        var cal = new RecoveredCalibrationJson(
            SchemaVersion: 1,
            Scale: 0.31536, RotationRadians: -3.14153,
            OriginX: 1039.45, OriginY: -36.38,
            MirrorNorth: false, CalibrationZoom: 1.0,
            ResidualPixels: 0.34, ReferenceCount: 4,
            Source: "UserRefinement",
            Inliers: System.Array.Empty<InlierJson>());

        var mapRect = new MapRect(OriginX: 130, OriginY: 60,
            Width: 1013, Height: 1001,
            TextureWidth: 2048, TextureHeight: 2033);

        var t = MapRectConversion.FromRecoveredCalibration(cal, mapRect);

        // Spot-check: world (0, 0, 0) projects to what we'd get composing the
        // canonical AreaCalibration with (MapRect.TextureToScreenshot − origin).
        var canonical = new AreaCalibration(
            cal.Scale, cal.RotationRadians, cal.OriginX, cal.OriginY,
            cal.ReferenceCount, cal.ResidualPixels) { MirrorNorth = cal.MirrorNorth };
        var texturePixel = canonical.WorldToWindow(new WorldCoord(0, 0, 0));
        var screenshotPixel = mapRect.TextureToScreenshot(texturePixel.X, texturePixel.Y);
        var expectedAlignedX = screenshotPixel.Sx - mapRect.OriginX;
        var expectedAlignedY = screenshotPixel.Sy - mapRect.OriginY;

        var actual = t.Apply(new WorldCoord(0, 0, 0));
        actual.X.Should().BeApproximately(expectedAlignedX, 1e-6);
        actual.Y.Should().BeApproximately(expectedAlignedY, 1e-6);
    }

    [Fact]
    public void Anisotropic_MapRect_warns_via_out_param()
    {
        var cal = new RecoveredCalibrationJson(
            1, Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
            MirrorNorth: false, CalibrationZoom: 1.0,
            ResidualPixels: 0.0, ReferenceCount: 1, Source: "UserRefinement",
            Inliers: System.Array.Empty<InlierJson>());

        // 10% anisotropic resize (X factor 0.5, Y factor 0.45).
        var mapRect = new MapRect(0, 0, 1000, 900, 2000, 2000);

        MapRectConversion.FromRecoveredCalibration(cal, mapRect, out var anisotropyPercent);
        anisotropyPercent.Should().BeGreaterThan(1.0);
    }
}
```

- [ ] **1B.2: Run, verify it fails**

Expected: BUILD FAILURE — `MapRectConversion` not found.

- [ ] **1B.3: Implement**

```csharp
// tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/MapRectConversion.cs
using System;
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

internal static class MapRectConversion
{
    /// <summary>
    /// Convert a production-recovered AreaCalibration (texture-pixel space) plus
    /// a MapRect (texture↔screenshot mapping) into a CandidateTransform that
    /// projects world coords into the aligned-pair-pixel space the synthesis
    /// probe's L_t fields live in. The aligned pair is the MapRect's crop minus
    /// its origin — i.e., a local (0, 0) coordinate system whose pixel (0, 0)
    /// is the top-left of the crop, with dimensions (Width, Height).
    ///
    /// MapRect.TextureToScreenshot scales texture coords by (Width/TextureWidth,
    /// Height/TextureHeight) and offsets by (OriginX, OriginY). The aligned-pair
    /// space is that minus the offset:
    ///
    ///     aligned_pair_x = texture_x * (Width / TextureWidth)
    ///     aligned_pair_y = texture_y * (Height / TextureHeight)
    ///
    /// CandidateTransform is isotropic-scale-only; if the X and Y resize ratios
    /// differ, the geometric mean is used and the difference is surfaced via
    /// <paramref name="anisotropyPercent"/>. Callers should warn if it exceeds
    /// roughly 1%.
    /// </summary>
    public static CandidateTransform FromRecoveredCalibration(
        RecoveredCalibrationJson cal,
        MapRect mapRect,
        out double anisotropyPercent)
    {
        double ratioX = (double)mapRect.Width / mapRect.TextureWidth;
        double ratioY = (double)mapRect.Height / mapRect.TextureHeight;
        double geom = Math.Sqrt(ratioX * ratioY);
        anisotropyPercent = 100.0 * Math.Abs(ratioX - ratioY) / geom;

        return new CandidateTransform(
            Scale: cal.Scale * geom,
            RotRadians: cal.RotationRadians,
            Mirror: cal.MirrorNorth,
            Tx: cal.OriginX * ratioX,
            Ty: cal.OriginY * ratioY);
    }

    /// <summary>Overload without the anisotropy out-param.</summary>
    public static CandidateTransform FromRecoveredCalibration(
        RecoveredCalibrationJson cal, MapRect mapRect)
        => FromRecoveredCalibration(cal, mapRect, out _);
}
```

- [ ] **1B.4: Run, verify passes**

Expected: PASS — 2 tests.

#### Sub-task 1C — BundleLoader + LoadedBundle

- [ ] **1C.1: Write the failing test**

```csharp
// tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Bundle/BundleLoaderTests.cs
using System.IO;
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Bundle;

public class BundleLoaderTests
{
    [Fact]
    public void Loads_full_bundle_with_recovered_cal()
    {
        var dir = NewBundleDir();
        try
        {
            WriteAttemptJson(dir, outcome: "accepted", includeRecoveredCal: true);
            WriteMapRectJson(dir);
            WriteRecoveredCalJson(dir);
            File.WriteAllBytes(Path.Combine(dir, "07-deviation.png"), new byte[1]); // placeholder

            var bundle = BundleLoader.Open(dir);

            bundle.Attempt.Outcome.Should().Be("accepted");
            bundle.Attempt.Area.Should().Be("AreaEltibule");
            bundle.MapRect.Should().NotBeNull();
            bundle.RecoveredCal.Should().NotBeNull();
            bundle.DeviationPath.Should().EndWith("07-deviation.png");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Loads_rejected_bundle_with_null_recovered_cal()
    {
        var dir = NewBundleDir();
        try
        {
            WriteAttemptJson(dir, outcome: "rejected-3inliers", includeRecoveredCal: false);
            WriteMapRectJson(dir);
            File.WriteAllBytes(Path.Combine(dir, "07-deviation.png"), new byte[1]);

            var bundle = BundleLoader.Open(dir);

            bundle.Attempt.Outcome.Should().Be("rejected-3inliers");
            bundle.MapRect.Should().NotBeNull();
            bundle.RecoveredCal.Should().BeNull("rejected attempts have no recovered-cal");
            bundle.DeviationPath.Should().EndWith("07-deviation.png");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Throws_when_attempt_json_missing()
    {
        var dir = NewBundleDir();
        try
        {
            var act = () => BundleLoader.Open(dir);
            act.Should().Throw<FileNotFoundException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewBundleDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-bundle-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteAttemptJson(string dir, string outcome, bool includeRecoveredCal)
    {
        var recoveredField = includeRecoveredCal ? "\"11-recovered-cal.json\"" : "null";
        File.WriteAllText(Path.Combine(dir, "01-attempt.json"), $$"""
            { "schemaVersion": 1,
              "area": "AreaEltibule",
              "attemptStartedUtc": "2026-06-02T01:00:00Z",
              "attemptFinalizedUtc": "2026-06-02T01:00:01Z",
              "outcome": "{{outcome}}",
              "rejectReason": null,
              "engineVersion": "1.0.0",
              "files": {
                "rawScreenshot": "02-screenshot-raw.png",
                "grayScreenshot": "03-screenshot-gray.png",
                "mapRect": "04-maprect.json",
                "baseTextureResampled": null,
                "alignedScreenshot": null,
                "deviation": "07-deviation.png",
                "detectionsImage": null,
                "projectionOverlay": null,
                "detections": null,
                "recoveredCalibration": {{recoveredField}}
              } }
            """);
    }

    private static void WriteMapRectJson(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "04-maprect.json"), """
            { "schemaVersion": 1, "originX": 130, "originY": 60,
              "width": 1013, "height": 1001,
              "textureWidth": 2048, "textureHeight": 2033,
              "autoDetectScore": null, "sourceScaleFactor": null }
            """);
    }

    private static void WriteRecoveredCalJson(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "11-recovered-cal.json"), """
            { "schemaVersion": 1, "scale": 0.31536, "rotationRadians": -3.14153,
              "originX": 1039.45, "originY": -36.38, "mirrorNorth": false,
              "calibrationZoom": 1.0, "residualPixels": 0.34,
              "referenceCount": 4, "source": "UserRefinement", "inliers": [] }
            """);
    }
}
```

- [ ] **1C.2: Run, verify it fails**

Expected: BUILD FAILURE — `BundleLoader` not found.

- [ ] **1C.3: Implement LoadedBundle + BundleLoader**

```csharp
// tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/LoadedBundle.cs
namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

internal sealed record LoadedBundle(
    string Directory,
    AttemptJson Attempt,
    MapRectJson? MapRect,
    RecoveredCalibrationJson? RecoveredCal,
    DetectionsJson? Detections,
    string? DeviationPath);
```

```csharp
// tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/BundleLoader.cs
using System.IO;
using System.Text.Json;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

internal static class BundleLoader
{
    public static LoadedBundle Open(string directory)
    {
        var attemptPath = Path.Combine(directory, "01-attempt.json");
        if (!File.Exists(attemptPath))
            throw new FileNotFoundException($"Bundle missing 01-attempt.json", attemptPath);

        var attempt = JsonSerializer.Deserialize(
            File.ReadAllText(attemptPath),
            BundleJsonContext.Default.AttemptJson)!;

        var mapRect = LoadOptionalJson(directory, attempt.Files.MapRect, BundleJsonContext.Default.MapRectJson);
        var recoveredCal = LoadOptionalJson(directory, attempt.Files.RecoveredCalibration, BundleJsonContext.Default.RecoveredCalibrationJson);
        var detections = LoadOptionalJson(directory, attempt.Files.Detections, BundleJsonContext.Default.DetectionsJson);

        string? deviationPath = attempt.Files.Deviation is { } name
            ? Path.Combine(directory, name)
            : null;
        if (deviationPath is not null && !File.Exists(deviationPath))
            deviationPath = null;

        return new LoadedBundle(directory, attempt, mapRect, recoveredCal, detections, deviationPath);
    }

    private static T? LoadOptionalJson<T>(
        string directory,
        string? fileName,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (fileName is null) return null;
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);
    }
}
```

- [ ] **1C.4: Run, verify passes**

Expected: PASS — 3 tests.

#### Task 1 wrap-up

- [ ] **1D: Run the full new-tests filter to confirm all 8 tests pass**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~Bundle"
```

Expected: PASS — 8 tests (3 + 2 + 3).

- [ ] **1E: Run the full test suite to confirm no regressions**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release
```

Expected: 37 passing (29 existing + 8 new), 0 failing.

- [ ] **1F: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/Bundle/ tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Bundle/
git commit -m "$(cat <<'EOF'
feat(synthesis-probe): bundle data layer — DTOs + loader + MapRect conversion

Replicates Mithril.MapCalibration.Capture.Diagnostics.CalibrationBundleJson's
DTO shapes in the tool (BundleJsonContext, source-generated). BundleLoader.Open
reads 01-attempt.json and follows its Files map to resolve typed JSON + the
deviation PNG. MapRectConversion.FromRecoveredCalibration converts a production
AreaCalibration (texture-pixel space) plus a MapRect into a CandidateTransform
that projects into the synthesis probe's aligned-pair-pixel field space.
Anisotropic resize ratios are surfaced via an out-param.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Probe consumers + CLI surface

This block adds the alternate field-build entry point and the five new CLI flags. Two independent extensions, but small enough to share one subagent + one commit.

**Files:**

- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/IconLikelihoodField.cs`
- Modify: `tools/MapCalibrationFromScreenshot/CliArgs.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/IconLikelihoodFieldLoadDeviationTests.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/CliArgsBundleFlagsTests.cs`

#### Sub-task 2A — IconLikelihoodField.LoadDeviationAsField

- [ ] **2A.1: Write the failing test**

```csharp
// tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/IconLikelihoodFieldLoadDeviationTests.cs
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class IconLikelihoodFieldLoadDeviationTests
{
    [Fact]
    public void LoadDeviationAsField_peaks_at_pre_subtracted_icon_location()
    {
        const int W = 64, H = 64;
        // Synthetic pre-subtracted deviation: black background, single 5x5
        // cross stamped at (32, 32). This is what the live engine produces
        // post-ECC, post-subtraction.
        var devPixels = new byte[W * H];
        StampCross(devPixels, W, cx: 32, cy: 32);
        var deviation = new GrayImage(W, H, devPixels);

        var template = MakeCrossTemplate();
        var field = IconLikelihoodField.LoadDeviationAsField(deviation, template);

        field.GetLength(0).Should().Be(H);
        field.GetLength(1).Should().Be(W);

        var (maxX, maxY) = Argmax(field);
        maxX.Should().BeInRange(31, 33);
        maxY.Should().BeInRange(31, 33);
        field[maxY, maxX].Should().BeGreaterThan(0.8);
    }

    private static void StampCross(byte[] pixels, int width, int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            pixels[(cy + dy) * width + cx] = 200;
        for (int dx = -2; dx <= 2; dx++)
            pixels[cy * width + (cx + dx)] = 200;
        pixels[cy * width + cx] = 255;
    }

    private static IconTemplate MakeCrossTemplate()
    {
        var gray = new byte[]   { 0,0,200,0,0,  0,0,200,0,0,  200,200,255,200,200,  0,0,200,0,0,  0,0,200,0,0 };
        var alpha = new byte[]  { 0,0,255,0,0,  0,0,255,0,0,  255,255,255,255,255,  0,0,255,0,0,  0,0,255,0,0 };
        return new IconTemplate(
            Name: "x", LandmarkType: "Portal", PivotX: 0.5, PivotY: 0.5,
            Gray: new GrayImage(5, 5, gray),
            Alpha: new GrayImage(5, 5, alpha));
    }

    private static (int X, int Y) Argmax(double[,] field)
    {
        int bestX = 0, bestY = 0; double bestV = double.NegativeInfinity;
        for (int y = 0; y < field.GetLength(0); y++)
            for (int x = 0; x < field.GetLength(1); x++)
                if (field[y, x] > bestV) { bestV = field[y, x]; bestX = x; bestY = y; }
        return (bestX, bestY);
    }
}
```

- [ ] **2A.2: Run, verify fails**

Expected: BUILD FAILURE — `LoadDeviationAsField` not found.

- [ ] **2A.3: Implement**

Add to `tools/MapCalibrationFromScreenshot/SynthesisProbe/IconLikelihoodField.cs`, right after `Build`:

```csharp
/// <summary>
/// Build a field from a pre-computed deviation map. Skips the screenshot-minus-
/// aligned-base subtraction step that <see cref="Build"/> performs; equivalent
/// to calling <see cref="ScoreAll"/> directly with a deviation supplied by the
/// caller. Used by the bundle-consumption path where the live engine has
/// already produced a post-ECC deviation via #978's ECC refiner.
/// </summary>
/// <returns>Same row-major [H, W] layout as <see cref="ScoreAll"/>.</returns>
public static double[,] LoadDeviationAsField(GrayImage deviation, IconTemplate template)
    => ScoreAll(deviation, template);
```

- [ ] **2A.4: Run, verify passes**

Expected: PASS — 1 test.

#### Sub-task 2B — Five CLI flags

- [ ] **2B.1: Write the failing test**

```csharp
// tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/CliArgsBundleFlagsTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class CliArgsBundleFlagsTests
{
    [Fact]
    public void Parses_bundle_dir()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--bundle-dir", "C:/bundles/foo",
        })!;
        args.BundleDir.Should().Be("C:/bundles/foo");
    }

    [Fact]
    public void Parses_maprect_json()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--maprect-json", "C:/bundles/foo/04-maprect.json",
        })!;
        args.MapRectJsonPath.Should().Be("C:/bundles/foo/04-maprect.json");
    }

    [Fact]
    public void Parses_recovered_cal_json()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--recovered-cal-json", "C:/bundles/foo/11-recovered-cal.json",
        })!;
        args.RecoveredCalJsonPath.Should().Be("C:/bundles/foo/11-recovered-cal.json");
    }

    [Fact]
    public void Parses_aligned_deviation()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--aligned-deviation", "C:/bundles/foo/07-deviation.png",
        })!;
        args.AlignedDeviationPath.Should().Be("C:/bundles/foo/07-deviation.png");
    }

    [Fact]
    public void Parses_detections_json()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--detections-json", "C:/bundles/foo/10-detections.json",
        })!;
        args.DetectionsJsonPath.Should().Be("C:/bundles/foo/10-detections.json");
    }

    [Fact]
    public void Parses_hand_truth_cal_five_tuple()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--hand-truth-cal", "0.7632,3.141276,2146.21,-202.47,false",
        })!;

        args.HandTruthCal.Should().NotBeNull();
        args.HandTruthCal!.Value.Scale.Should().BeApproximately(0.7632, 1e-9);
        args.HandTruthCal.Value.Rot.Should().BeApproximately(3.141276, 1e-9);
        args.HandTruthCal.Value.Ox.Should().BeApproximately(2146.21, 1e-9);
        args.HandTruthCal.Value.Oy.Should().BeApproximately(-202.47, 1e-9);
        args.HandTruthCal.Value.Mirror.Should().BeFalse();
    }
}
```

- [ ] **2B.2: Run, verify fails**

Expected: BUILD FAILURE — five new properties not on `CliArgs`.

- [ ] **2B.3: Add the five fields + parser cases**

In `CliArgs.cs`, after the existing `AlignedBasePath` field on the record:

```csharp
string? BundleDir,
string? MapRectJsonPath,
string? RecoveredCalJsonPath,
string? AlignedDeviationPath,
string? DetectionsJsonPath,
(double Scale, double Rot, double Ox, double Oy, bool Mirror)? HandTruthCal
```

In `Parse`, alongside the existing `--aligned-base` case:

```csharp
case "--bundle-dir":
    bundleDir = Next(argv, ref i);
    break;
case "--maprect-json":
    mapRectJsonPath = Next(argv, ref i);
    break;
case "--recovered-cal-json":
    recoveredCalJsonPath = Next(argv, ref i);
    break;
case "--aligned-deviation":
    alignedDeviationPath = Next(argv, ref i);
    break;
case "--detections-json":
    detectionsJsonPath = Next(argv, ref i);
    break;
case "--hand-truth-cal":
    handTruthCal = ParseTruthCal(Next(argv, ref i));  // reuse the existing 'scale,rot,ox,oy,mirror' parser from --truth-cal
    break;
```

Add the local declarations alongside `alignedBasePath`:

```csharp
string? bundleDir = null;
string? mapRectJsonPath = null;
string? recoveredCalJsonPath = null;
string? alignedDeviationPath = null;
string? detectionsJsonPath = null;
(double, double, double, double, bool)? handTruthCal = null;
```

Pass the six new fields to the constructor at the bottom of `Parse`. Update help text:

```
  --bundle-dir <dir>                    a per-attempt diagnostic bundle directory written by Mithril's
                                         AutoCalibrationEngine. Auto-resolves --maprect-json /
                                         --recovered-cal-json / --aligned-deviation / --detections-json
                                         from the bundle's 01-attempt.json manifest if those flags aren't
                                         given explicitly.
  --maprect-json <path>                 the bundle's 04-maprect.json (texture↔aligned-pair transform).
  --recovered-cal-json <path>           the bundle's 11-recovered-cal.json (production-recovered
                                         AreaCalibration). When given with --maprect-json, the
                                         synthesis-probe derives --truth-cal automatically.
  --aligned-deviation <path>            the bundle's 07-deviation.png (post-ECC, post-subtraction
                                         deviation). Bypasses IconLikelihoodField.Build's subtraction.
  --detections-json <path>              the bundle's 10-detections.json (production's NCC detection set).
                                         Optional; not consumed in v1.
  --hand-truth-cal <scale,rot,ox,oy,mirror>
                                         user-supplied texture-pixel-space truth cal (mirror = true|false).
                                         Converted to crop-pixel via --maprect-json before use. Takes
                                         precedence over --recovered-cal-json (use when production's
                                         recovered cal is known-wrong, e.g. the 2026-06-02 4-inlier
                                         residual-4-px solves; supply the hand-verified entry from
                                         src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json).
```

- [ ] **2B.4: Run, verify passes**

Expected: PASS — 6 tests.

#### Task 2 wrap-up

- [ ] **2C: Run all tests to confirm no regressions**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release
```

Expected: 44 passing (37 from Task 1 + 1 deviation + 6 CLI), 0 failing.

- [ ] **2D: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/IconLikelihoodField.cs tools/MapCalibrationFromScreenshot/CliArgs.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/IconLikelihoodFieldLoadDeviationTests.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/CliArgsBundleFlagsTests.cs
git commit -m "$(cat <<'EOF'
feat(synthesis-probe): LoadDeviationAsField alt + five bundle-consumption flags

LoadDeviationAsField skips Build's screenshot-minus-base step when the live
engine has already produced a post-ECC deviation (#978). Five new CLI flags
(--bundle-dir + --maprect-json / --recovered-cal-json / --aligned-deviation
/ --detections-json) expose the bundle artifacts to the probe.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Integration + E5 bracket + README

This block ties everything together: the wiring in `SynthesisProbePhase`, the new `E5_ColdGrid.BracketAroundExpected` helper that uses the MapRect-implied scale, and the README documentation. All three changes are part of the "consumption now works end-to-end" story; bundling them into one commit makes git history clean.

**Files:**

- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs`
- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E5_ColdGrid.cs`
- Modify: `tools/MapCalibrationFromScreenshot/README.md`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Experiments/E5_ColdGrid_BracketedTests.cs`

#### Sub-task 3A — E5 BracketAroundExpected

- [ ] **3A.1: Write the failing test**

```csharp
// tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Experiments/E5_ColdGrid_BracketedTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Experiments;

public class E5_ColdGrid_BracketedTests
{
    [Fact]
    public void Bracketed_scaleRange_centers_at_expected_scale()
    {
        var portal = new double[64, 64];
        portal[20, 20] = 0.9;
        var fields = new System.Collections.Generic.Dictionary<string, double[,]> { ["Portal"] = portal };
        var refs = new[] { new ReferencePoint("p1", "Portal", 0, 0) };
        var truth = new CandidateTransform(0.5, 0.0, false, 20, 20);

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "synth-probe-e5-bracket-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            using var w = new SynthesisProbeWriter(dir);
            var report = E5_ColdGrid.Run(
                fields, refs, truth,
                scaleBracket: E5_ColdGrid.BracketAroundExpected(0.5, fractionAbove: 0.2),
                scaleSamples: 8,
                cropWidth: 64, cropHeight: 64,
                gridStepPx: 8,
                templateSizePx: 5,
                writer: w);

            // All explored scales must be within ±20% of 0.5 → [0.4, 0.6].
            foreach (var (t, _, _) in report.Top8AfterRefine)
            {
                t.Scale.Should().BeInRange(0.4 * 0.99, 0.6 * 1.01,
                    "bracketed E5 must not consider scales outside ±20% of expected");
            }
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **3A.2: Run, verify fails**

Expected: BUILD FAILURE — `E5_ColdGrid.BracketAroundExpected` not found.

- [ ] **3A.3: Implement**

In `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E5_ColdGrid.cs`, add to the class:

```csharp
/// <summary>
/// Compute a tight scaleBracket around an expected scale (typically derived
/// from the MapRect's resize ratio + production AreaCalibration). Excludes the
/// tiny-scale degeneracy by construction — the synthesis objective's worst
/// failure mode is at scales orders of magnitude below truth, which never
/// arise inside a physically-plausible bracket.
/// </summary>
public static (double Min, double Max) BracketAroundExpected(double expected, double fractionAbove)
    => (expected * (1.0 - fractionAbove), expected * (1.0 + fractionAbove));
```

The existing `Run` method already accepts `scaleBracket: (double Min, double Max)` — no signature change.

- [ ] **3A.4: Run, verify passes**

Expected: PASS — 1 test.

#### Sub-task 3B — SynthesisProbePhase wiring

- [ ] **3B.1: Read the current `SynthesisProbePhase.cs`**

Read the file fully. The existing wiring is the entry point added in yesterday's Task 14; you're extending it.

- [ ] **3B.2: Add the bundle-resolution + truth-cal-derivation branches**

Insert this logic near the top of `Run`, after validating `args.Area`:

```csharp
// Resolve --bundle-dir into the four file paths if not explicitly overridden.
LoadedBundle? loadedBundle = null;
string? mapRectJsonPath = args.MapRectJsonPath;
string? recoveredCalJsonPath = args.RecoveredCalJsonPath;
string? alignedDeviationPath = args.AlignedDeviationPath;
string? detectionsJsonPath = args.DetectionsJsonPath;

if (!string.IsNullOrEmpty(args.BundleDir))
{
    loadedBundle = BundleLoader.Open(args.BundleDir);
    mapRectJsonPath ??= loadedBundle.Attempt.Files.MapRect is { } mr ? Path.Combine(args.BundleDir, mr) : null;
    recoveredCalJsonPath ??= loadedBundle.Attempt.Files.RecoveredCalibration is { } rc ? Path.Combine(args.BundleDir, rc) : null;
    alignedDeviationPath ??= loadedBundle.DeviationPath;
    detectionsJsonPath ??= loadedBundle.Attempt.Files.Detections is { } d ? Path.Combine(args.BundleDir, d) : null;
}

// Derive truth-cal per the precedence in the plan header:
//   1. --truth-cal           (crop-pixel space, wins outright)
//   2. --hand-truth-cal      (texture-pixel space + MapRect → conversion)
//   3. --recovered-cal-json  (texture-pixel space + MapRect → conversion)
// All three of cases 2/3 share the same MapRectConversion path.
CandidateTransform? truth = null;

if (args.TruthCal is { } tc)
{
    truth = new CandidateTransform(tc.Scale, tc.Rot, tc.Mirror, tc.Ox, tc.Oy);
}
else if (args.HandTruthCal is { } htc)
{
    if (mapRectJsonPath is null)
    {
        Console.Error.WriteLine("[err] --hand-truth-cal requires --maprect-json (directly or via --bundle-dir).");
        return 2;
    }
    var mapRectJson = JsonSerializer.Deserialize(
        File.ReadAllText(mapRectJsonPath),
        BundleJsonContext.Default.MapRectJson)!;
    var mapRect = new MapRect(
        mapRectJson.OriginX, mapRectJson.OriginY,
        mapRectJson.Width, mapRectJson.Height,
        mapRectJson.TextureWidth, mapRectJson.TextureHeight,
        mapRectJson.AutoDetectScore, mapRectJson.SourceScaleFactor);
    var handCalJson = new RecoveredCalibrationJson(
        SchemaVersion: 1,
        Scale: htc.Scale, RotationRadians: htc.Rot,
        OriginX: htc.Ox, OriginY: htc.Oy,
        MirrorNorth: htc.Mirror,
        CalibrationZoom: 1.0,
        ResidualPixels: 0.0,
        ReferenceCount: 0,
        Source: "HandSupplied",
        Inliers: System.Array.Empty<InlierJson>());
    truth = MapRectConversion.FromRecoveredCalibration(handCalJson, mapRect, out var handAnisoPct);
    if (handAnisoPct > 1.0)
        Console.Error.WriteLine($"[warn] MapRect resize is anisotropic by {handAnisoPct:0.00}%; using geometric mean.");
    Console.Error.WriteLine("[truth] using --hand-truth-cal (texture-pixel space → crop-pixel via MapRect).");
}
else if (mapRectJsonPath is not null && recoveredCalJsonPath is not null)
{
    var mapRectJson = JsonSerializer.Deserialize(
        File.ReadAllText(mapRectJsonPath),
        BundleJsonContext.Default.MapRectJson)!;
    var recoveredCalJson = JsonSerializer.Deserialize(
        File.ReadAllText(recoveredCalJsonPath),
        BundleJsonContext.Default.RecoveredCalibrationJson)!;
    var mapRect = new MapRect(
        mapRectJson.OriginX, mapRectJson.OriginY,
        mapRectJson.Width, mapRectJson.Height,
        mapRectJson.TextureWidth, mapRectJson.TextureHeight,
        mapRectJson.AutoDetectScore, mapRectJson.SourceScaleFactor);
    truth = MapRectConversion.FromRecoveredCalibration(recoveredCalJson, mapRect, out var anisoPct);
    if (anisoPct > 1.0)
        Console.Error.WriteLine($"[warn] MapRect resize is anisotropic by {anisoPct:0.00}%; using geometric mean.");
    Console.Error.WriteLine(
        $"[truth] using --recovered-cal-json (production's recovered cal, residual {recoveredCalJson.ResidualPixels:0.00} px). " +
        "If production's solve is suspect, override with --hand-truth-cal.");
}

if (truth is null)
{
    Console.Error.WriteLine("[err] No truth-cal: pass --truth-cal, or --hand-truth-cal + --maprect-json, or --bundle-dir/--recovered-cal-json + --maprect-json.");
    return 2;
}
```

Then later, where the existing code does `IconLikelihoodField.Build(screenshotCrop, alignedBase, template)` for each template, branch on `alignedDeviationPath`:

```csharp
var fieldsByType = new Dictionary<string, double[,]>();
if (alignedDeviationPath is not null)
{
    var deviation = ImageIo.LoadGray(alignedDeviationPath);
    foreach (var template in templates)
    {
        using var fieldSpan = SynthesisProbeTracer.Source.StartActivity("field.build");
        fieldSpan?.SetTag("template.type", template.LandmarkType);
        fieldSpan?.SetTag("template.size_px", template.Gray.Width);
        fieldSpan?.SetTag("source", "aligned-deviation");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        fieldsByType[template.LandmarkType] = IconLikelihoodField.LoadDeviationAsField(deviation, template);
        fieldSpan?.SetTag("duration_ms", sw.ElapsedMilliseconds);
    }
}
else
{
    // ... existing Build(...) path stays here unchanged ...
}
```

Wire the new `truth!.Value` through to the experiment calls (the experiments already accept a `CandidateTransform` — verify their signatures and call sites haven't drifted).

Replace the existing E5 call's `scaleBracket: (0.1, 2.0)` with a bracketed call:

```csharp
var scaleBracket = E5_ColdGrid.BracketAroundExpected(truth.Value.Scale, fractionAbove: 0.2);
E5_ColdGrid.Run(fieldsByType, refs, truth.Value,
    scaleBracket: scaleBracket,
    scaleSamples: 16,
    cropWidth: mw, cropHeight: mh,
    gridStepPx: 16,
    templateSizePx: 16,
    writer: writer);
```

- [ ] **3B.3: Build + run all tests**

```bash
dotnet build tools/MapCalibrationFromScreenshot -c Release
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --no-build
```

Expected: BUILD SUCCESS (0 warnings), all 45 tests pass (43 from Task 2 + 1 from E5 bracket), 0 failed.

- [ ] **3B.4: Smoke-test the CLI surface manually**

```bash
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- --phase synthesis-probe --area AreaEltibule
```

Expected: stderr `[err] No truth-cal: …`, exit code 2.

```bash
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- --phase synthesis-probe --area AreaEltibule --bundle-dir /nonexistent
```

Expected: a `FileNotFoundException` for `01-attempt.json`.

#### Sub-task 3C — README

- [ ] **3C.1: Append a section for the bundle-driven workflow**

Find the existing `## --phase synthesis-probe — icon-likelihood-field diagnostic` section. Append, just before its closing line:

````markdown
### Bundle-driven workflow (after PR #985)

The synthesis probe consumes the per-attempt diagnostic bundles Mithril's live engine writes (`%LocalAppData%/Mithril/diagnostics/calibration/<Area>-<timestamp>-<outcome>/`). For a single attempt:

```powershell
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- `
  --phase synthesis-probe `
  --area AreaEltibule `
  --bundle-dir "C:/Users/<you>/AppData/Local/Mithril/diagnostics/calibration/AreaEltibule-20260602-1230-accepted"
```

`--bundle-dir` resolves the rest from the bundle's `01-attempt.json` manifest:

| Bundle file | Probe flag (auto-resolved) |
|---|---|
| `04-maprect.json` | `--maprect-json` |
| `07-deviation.png` | `--aligned-deviation` |
| `11-recovered-cal.json` | `--recovered-cal-json` |
| `10-detections.json` | `--detections-json` (not consumed in v1) |

When both `--maprect-json` and `--recovered-cal-json` are present, the probe derives truth-cal automatically — no manual `--truth-cal` needed.

If production's recovered cal is known-wrong (e.g. a 4-inlier, residual-≈4-px solve that geometrically misaligns the overlay), override with `--hand-truth-cal scale,rot,ox,oy,mirror` — texture-pixel-space, gets converted via the same MapRect to the field's coord space. Source for hand-verified Eltibule: `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`.

When `--aligned-deviation` is present, the probe skips `IconLikelihoodField.Build`'s screenshot-minus-base subtraction and runs `ScoreAll` directly on the bundle's post-ECC deviation.

E5's scale bracket auto-narrows to ±20% of the expected aligned-pair-pixel scale (derived from the MapRect's resize ratio), excluding the tiny-scale degeneracy by construction.

Override any auto-resolved flag by passing it explicitly — the explicit flag wins.
````

#### Task 3 wrap-up

- [ ] **3D: Final build + full test run**

```bash
dotnet build Mithril.slnx -c Release 2>&1 | tail -5
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --no-build 2>&1 | tail -5
```

Expected: 0 warnings / 0 errors in build; 45/45 passing in the probe test project.

- [ ] **3E: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E5_ColdGrid.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Experiments/E5_ColdGrid_BracketedTests.cs tools/MapCalibrationFromScreenshot/README.md
git commit -m "$(cat <<'EOF'
feat(synthesis-probe): integrate bundle consumption + bracket E5 by MapRect

SynthesisProbePhase now branches on --bundle-dir / --aligned-deviation /
--recovered-cal-json + --maprect-json. Truth-cal is derived automatically
when the bundle supplies both MapRect and recovered cal. The aligned-
deviation path skips Build's screenshot-minus-base subtraction. E5's
scale bracket is ±20% around the MapRect-implied expected scale,
excluding the tiny-scale degeneracy (yesterday's diagnostic finding).

README documents the bundle-driven workflow.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**Spec coverage:**

- §"What we have right now" + §"What a clean diagnostic of the synthesis objective would need" from the synthesis-probe spec's Results section → Task 1 (DTOs/Loader/Converter), Task 3 (wiring).
- §"Open questions" item 1 (`--aligned-deviation`) → Task 2 (LoadDeviationAsField + flag), Task 3 (wiring).
- §"Open questions" item 2 (truth-cal extraction) → Task 1 (MapRectConversion), Task 3 (wiring).
- §"Open questions" item 3 (E5 bracket tightening) → Task 3 (BracketAroundExpected + wiring).
- §"Open questions" item 5 (test pair captures) → out-of-scope here; depends on user-side capture once new dumper is live (parallel to this work).

**Placeholder scan:** None. All steps have concrete code blocks. The "existing Build(...) path stays here unchanged" reference in Task 3B is a scoping marker (engineer reads the file in 3B.1) but doesn't hide any specific code that needs to be written.

**Type consistency:**

- `CandidateTransform(Scale, RotRadians, Mirror, Tx, Ty)` — used consistently throughout.
- `LoadedBundle(Directory, Attempt, MapRect, RecoveredCal, Detections, DeviationPath)` — Task 1 defines, Task 3 consumes.
- `MapRectConversion.FromRecoveredCalibration(cal, mapRect, out anisoPct)` — Task 1 defines, Task 3 consumes.
- `E5_ColdGrid.BracketAroundExpected(expected, fractionAbove)` — defined in 3A.3, used in 3B.2.
- `IconLikelihoodField.LoadDeviationAsField(deviation, template)` — defined in 2A.3, used in 3B.2.

All consistent.

**Open assumption** (flagged at execution time): the bundle's `07-deviation.png` is a single-channel byte PNG that `ImageIo.LoadGray` decodes correctly. PR #985's visualizer is WPF-based and the production deviation export is gray, but if the actual file turns out to be RGB-encoded grayscale, `LoadGray` may need a small adaptation. The Task 2A test (synthetic deviation) won't catch this; Task 3B's smoke test on a real bundle will.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-06-02-synthesis-probe-bundle-consumption.md`.

Three task blocks, each one subagent dispatch:

- **Task 1** — bundle data layer (DTOs + Loader + Converter, 8 tests)
- **Task 2** — probe consumers + CLI surface (1 new test + 6 new tests including `--hand-truth-cal`)
- **Task 3** — integration + E5 bracket + README (1 new test + smoke + docs; wires the 3-tier truth-cal precedence)

Two execution options:

1. **Subagent-Driven (recommended)** — three subagent dispatches with two-stage review per block.
2. **Inline Execution** — `superpowers:executing-plans` with checkpoints.

Which approach?
