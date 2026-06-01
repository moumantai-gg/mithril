# Synthesis-Probe Diagnostic Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a `--phase synthesis-probe` in `tools/MapCalibrationFromScreenshot` that scores the icon-likelihood-field objective `J(T) = Σ L_{type(r)}(T·r)` across five experiments (E1–E5) on the committed Frame 1 / Frame 2 fixtures, dumps CSV + PNG artifacts and OTel spans, and tells us whether to pursue the cold or hybrid synthesis solver in production.

**Architecture:** Phase-additive — a new `SynthesisProbe/` subdirectory inside the existing tool project holds the field builder, J evaluator, gradient-ascent refiner, the five experiments, OTel tracer setup, and CSV/PNG output writers. A sibling test project (`MapCalibrationFromScreenshot.SynthesisProbe.Tests`) hosts xUnit/FluentAssertions tests, built outside `Mithril.slnx` (matching the parent tool's isolation). Production code under `src/Mithril.MapCalibration/**` is **not** touched in v0 — the spec is explicit.

**Tech Stack:** .NET 10 / `net10.0-windows`, xUnit + FluentAssertions for tests, OpenTelemetry SDK for .NET (`OpenTelemetry`, `OpenTelemetry.Exporter.Console`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`).

**Spec:** [docs/superpowers/specs/2026-06-01-synthesis-probe-diagnostic-design.md](../specs/2026-06-01-synthesis-probe-diagnostic-design.md). Territory map: `scratch/auto-calibration-handoff.md`.

---

## File Structure

### New files (tool project, `tools/MapCalibrationFromScreenshot/`)

| File | Responsibility |
|---|---|
| `SynthesisProbe/CandidateTransform.cs` | Record of `(Scale, RotRadians, Mirror, Tx, Ty)` + `Apply(WorldCoord) → PixelPoint` that mirrors `AreaCalibration.WorldToWindow`. |
| `SynthesisProbe/IconLikelihoodField.cs` | Builds `L_t` for one template: `D = clamp(screenshot − aligned_base, 0, 255)` then full-image alpha-masked NCC → `double[,]`. Plus `Sample(field, x, y)` with bicubic interpolation. |
| `SynthesisProbe/JEvaluator.cs` | `Evaluate(transform, fieldsByType, refs, mapRect) → JResult { J, RefsAboveHalf, RefsOffCrop, PerRefScores }`. Owns the world→texture→crop math. |
| `SynthesisProbe/LocalRefine.cs` | Gauss-Newton ascent on `(Scale, Tx, Ty)` over the bicubic field at a fixed `(Rot, Mirror)`. ~30 iter max, returns refined transform + final J. |
| `SynthesisProbe/RansacSeedsCsv.cs` | Reads `label,scale,rot,ox,oy,mirror` rows from a CSV into a list of `(label, CandidateTransform)`. |
| `SynthesisProbe/ProbeReferences.cs` | Loads area refs (landmarks + npcs) and types them with the same `LandmarkType` vocabulary the production detector uses. |
| `SynthesisProbe/SynthesisProbeWriter.cs` | Owns the CSV stream + the field/landscape PNG writers. |
| `SynthesisProbe/SynthesisProbeTracer.cs` | `ActivitySource MithrilToolsMapCalibrationSynthesisProbe = new("Mithril.Tools.MapCalibrationSynthesisProbe")` + `Configure(TraceConsole/Otlp option) → TracerProvider`. |
| `SynthesisProbe/Experiments/E1_TruthScore.cs` | One `Evaluate` at `--truth-cal`. |
| `SynthesisProbe/Experiments/E2_TranslationSweep.cs` | Sweeps `(Tx, Ty)` ±2·template_size around truth, step 1 px. Writes CSV + landscape PNG. |
| `SynthesisProbe/Experiments/E3_ScaleSweep.cs` | Sweeps `Scale` ±25% around truth, step 1%. Writes CSV + 1-D landscape PNG. |
| `SynthesisProbe/Experiments/E4_RansacSeedScore.cs` | Evaluates each row of `--ransac-seeds-csv`. |
| `SynthesisProbe/Experiments/E5_ColdGrid.cs` | Cold grid over `Scale × Tx × Ty × {Rot} × {Mirror}`, takes top-8 by raw `J`, refines each with `LocalRefine`, reports best post-refine distance to truth. |
| `SynthesisProbe/SynthesisProbePhase.cs` | Top-level: load inputs, build fields, configure tracer, run E1→E5, close tracer. |

### New files (test project, `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/`)

| File | Tests |
|---|---|
| `MapCalibrationFromScreenshot.SynthesisProbe.Tests.csproj` | Standalone csproj, references the tool and the production `Mithril.MapCalibration`. Built outside `Mithril.slnx`. |
| `CandidateTransformTests.cs` | `Apply` matches `AreaCalibration.WorldToWindow` exactly for both mirror values and a non-zero rotation. |
| `IconLikelihoodFieldTests.cs` | Synthetic 64×64 background, 1 known icon at (32, 32); field's argmax is at (32, 32). Bicubic `Sample` matches grid value at integer coords, interpolates smoothly at sub-pixel. |
| `JEvaluatorTests.cs` | Synthetic 3-ref scene; `J(truth) ≫ J(random transform)`; `RefsOffCrop` correctly counts off-crop refs. |
| `LocalRefineTests.cs` | From a 5 px offset on a synthetic gaussian-peak field, refine converges to within 0.5 px in ≤30 iter. |
| `E5_ColdGridTests.cs` | Synthetic 3-ref scene; top-8 grid maxima after refine include a ≤5 px-of-truth entry. |
| `SynthesisProbeTracerTests.cs` | When a listener is attached, `probe.attempt` span is emitted with expected tags. When no listener, no exception. |

### Modified files

- `tools/MapCalibrationFromScreenshot/MapCalibrationFromScreenshot.csproj` — add `OpenTelemetry`, `OpenTelemetry.Exporter.Console`, `OpenTelemetry.Exporter.OpenTelemetryProtocol` package refs.
- `tools/MapCalibrationFromScreenshot/CliArgs.cs` — add fields for `--truth-cal`, `--ransac-seeds-csv`, `--trace-console`, `--otlp`, plus `Phase.SynthesisProbe` enum value, parsing, help text.
- `tools/MapCalibrationFromScreenshot/Pipeline.cs` — dispatch `Phase.SynthesisProbe` to `SynthesisProbePhase.Run(args)` before the existing full-pipeline code.
- `tools/MapCalibrationFromScreenshot/README.md` — document `--phase synthesis-probe` at the bottom of the existing flag table.

---

### Task 1: Scaffold the phase, test project, and stub entry point

**Files:**
- Modify: `tools/MapCalibrationFromScreenshot/CliArgs.cs`
- Modify: `tools/MapCalibrationFromScreenshot/Pipeline.cs`
- Modify: `tools/MapCalibrationFromScreenshot/MapCalibrationFromScreenshot.csproj`
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/MapCalibrationFromScreenshot.SynthesisProbe.Tests.csproj`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/ScaffoldTests.cs`

- [ ] **Step 1: Create the test csproj**

```xml
<!-- tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/MapCalibrationFromScreenshot.SynthesisProbe.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
    <NoWarn>$(NoWarn);CA1416</NoWarn>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="FluentAssertions" Version="6.12.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\MapCalibrationFromScreenshot\MapCalibrationFromScreenshot.csproj" />
    <ProjectReference Include="..\..\src\Mithril.MapCalibration\Mithril.MapCalibration.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add a placeholder failing test**

```csharp
// tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/ScaffoldTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Phase_synthesis_probe_is_a_recognized_phase_value()
    {
        Enum.IsDefined(typeof(Phase), Phase.SynthesisProbe).Should().BeTrue();
    }
}
```

- [ ] **Step 3: Run the test, verify it fails**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release
```

Expected: BUILD FAILURE — `Phase.SynthesisProbe` does not exist.

- [ ] **Step 4: Add the enum value, the CLI parse case, and the help string**

In `tools/MapCalibrationFromScreenshot/CliArgs.cs`, add `SynthesisProbe` to the `Phase` enum:

```csharp
internal enum Phase
{
    Full,
    ExtractIcons,
    ExtractMap,
    SelfTest,
    EmitTemplates,
    SynthesisProbe,
}
```

In the same file's `ParsePhase` switch:

```csharp
private static Phase ParsePhase(string s) => s.ToLowerInvariant() switch
{
    "extract-icons" => Phase.ExtractIcons,
    "extract-map" => Phase.ExtractMap,
    "full" => Phase.Full,
    "self-test" => Phase.SelfTest,
    "emit-templates" => Phase.EmitTemplates,
    "synthesis-probe" => Phase.SynthesisProbe,
    _ => throw new UserFacingException($"unknown phase '{s}' (extract-icons | extract-map | full | self-test | emit-templates | synthesis-probe)"),
};
```

In the help text (`PrintUsage` modes block), append:

```
  --phase synthesis-probe       run E1-E5 icon-likelihood-field diagnostic; emits CSV + PNGs + OTel
```

- [ ] **Step 5: Create the stub phase entry**

```csharp
// tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs
namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class SynthesisProbePhase
{
    public static int Run(CliArgs args)
    {
        Console.WriteLine("[synthesis-probe] not yet implemented");
        return 0;
    }
}
```

- [ ] **Step 6: Dispatch in `Pipeline.cs`**

In `Pipeline.Run`, after the `SelfTest` block, add:

```csharp
if (args.Phase == Phase.SynthesisProbe)
{
    return SynthesisProbe.SynthesisProbePhase.Run(args);
}
```

- [ ] **Step 7: Add OTel package refs to the tool csproj**

In `tools/MapCalibrationFromScreenshot/MapCalibrationFromScreenshot.csproj`, inside the existing `<ItemGroup>` with `<PackageReference>` entries:

```xml
<PackageReference Include="OpenTelemetry" Version="1.10.0" />
<PackageReference Include="OpenTelemetry.Exporter.Console" Version="1.10.0" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.10.0" />
```

- [ ] **Step 8: Run the test, verify it passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release
```

Expected: PASS — 1 test, 0 failures.

- [ ] **Step 9: Verify the CLI accepts the new phase**

```bash
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- --phase synthesis-probe --area AreaEltibule
```

Expected: stdout contains `[synthesis-probe] not yet implemented`, exit code 0.

- [ ] **Step 10: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/CliArgs.cs tools/MapCalibrationFromScreenshot/Pipeline.cs tools/MapCalibrationFromScreenshot/MapCalibrationFromScreenshot.csproj tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/
git commit -m "feat(synthesis-probe): scaffold --phase synthesis-probe + test project"
```

---

### Task 2: CandidateTransform record

**Files:**
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/CandidateTransform.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/CandidateTransformTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// CandidateTransformTests.cs
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class CandidateTransformTests
{
    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(true, 0.0)]
    [InlineData(false, Math.PI)]
    [InlineData(true, Math.PI)]
    public void Apply_matches_AreaCalibration_WorldToWindow(bool mirror, double rot)
    {
        var t = new CandidateTransform(Scale: 0.82, RotRadians: rot, Mirror: mirror, Tx: 100.0, Ty: 200.0);
        var cal = new AreaCalibration(0.82, rot, 100.0, 200.0, ReferenceCount: 1, ResidualPixels: 0.0) { MirrorNorth = mirror };
        var world = new WorldCoord(50, 0, 30);

        var fromCandidate = t.Apply(world);
        var fromCalibration = cal.WorldToWindow(world);

        fromCandidate.X.Should().BeApproximately(fromCalibration.X, 1e-9);
        fromCandidate.Y.Should().BeApproximately(fromCalibration.Y, 1e-9);
    }

    [Fact]
    public void FromAreaCalibration_copies_all_fields()
    {
        var cal = new AreaCalibration(0.5, Math.PI, 12.0, 34.0, 5, 0.7) { MirrorNorth = true };
        var t = CandidateTransform.FromAreaCalibration(cal);

        t.Scale.Should().Be(0.5);
        t.RotRadians.Should().Be(Math.PI);
        t.Mirror.Should().BeTrue();
        t.Tx.Should().Be(12.0);
        t.Ty.Should().Be(34.0);
    }
}
```

- [ ] **Step 2: Run, verify it fails**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~CandidateTransformTests"
```

Expected: BUILD FAILURE — `CandidateTransform` not found.

- [ ] **Step 3: Implement**

```csharp
// CandidateTransform.cs
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal readonly record struct CandidateTransform(double Scale, double RotRadians, bool Mirror, double Tx, double Ty)
{
    public PixelPoint Apply(WorldCoord world)
    {
        var east = world.X;
        var north = Mirror ? -world.Z : world.Z;
        var cos = Math.Cos(RotRadians);
        var sin = Math.Sin(RotRadians);
        var rotE = east * cos + north * sin;
        var rotN = -east * sin + north * cos;
        return new PixelPoint(Tx + Scale * rotE, Ty - Scale * rotN);
    }

    public static CandidateTransform FromAreaCalibration(AreaCalibration cal) =>
        new(cal.Scale, cal.RotationRadians, cal.MirrorNorth, cal.OriginX, cal.OriginY);
}
```

- [ ] **Step 4: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~CandidateTransformTests"
```

Expected: PASS — 5 tests (4 theory rows + 1 fact), 0 failures.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/CandidateTransform.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/CandidateTransformTests.cs
git commit -m "feat(synthesis-probe): add CandidateTransform mirroring AreaCalibration"
```

---

### Task 3: CLI flag plumbing for --truth-cal, --ransac-seeds-csv, --trace-console, --otlp

**Files:**
- Modify: `tools/MapCalibrationFromScreenshot/CliArgs.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/CliArgsSynthesisProbeTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// CliArgsSynthesisProbeTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class CliArgsSynthesisProbeTests
{
    [Fact]
    public void Parses_truth_cal_five_tuple()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe",
            "--screenshot", "x.png",
            "--area", "AreaEltibule",
            "--truth-cal", "0.82,0.0,100.5,200.5,false",
        })!;

        args.TruthCal.Should().NotBeNull();
        args.TruthCal!.Value.Scale.Should().Be(0.82);
        args.TruthCal.Value.Rot.Should().Be(0.0);
        args.TruthCal.Value.Ox.Should().Be(100.5);
        args.TruthCal.Value.Oy.Should().Be(200.5);
        args.TruthCal.Value.Mirror.Should().BeFalse();
    }

    [Fact]
    public void Parses_ransac_seeds_csv_path()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe",
            "--screenshot", "x.png",
            "--area", "AreaEltibule",
            "--ransac-seeds-csv", "C:/seeds.csv",
        })!;
        args.RansacSeedsCsvPath.Should().Be("C:/seeds.csv");
    }

    [Fact]
    public void Parses_trace_console_flag()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--screenshot", "x.png", "--area", "AreaEltibule",
            "--trace-console",
        })!;
        args.TraceConsole.Should().BeTrue();
    }

    [Fact]
    public void Parses_otlp_endpoint()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--screenshot", "x.png", "--area", "AreaEltibule",
            "--otlp", "http://localhost:4317",
        })!;
        args.OtlpEndpoint.Should().Be("http://localhost:4317");
    }
}
```

- [ ] **Step 2: Run, verify it fails**

Expected: BUILD FAILURE — fields `TruthCal`, `RansacSeedsCsvPath`, `TraceConsole`, `OtlpEndpoint` not on `CliArgs`.

- [ ] **Step 3: Add the four fields + parsers to `CliArgs`**

In `CliArgs.cs`, add to the record:

```csharp
(double Scale, double Rot, double Ox, double Oy, bool Mirror)? TruthCal,
string? RansacSeedsCsvPath,
bool TraceConsole,
string? OtlpEndpoint
```

In `Parse`, add the locals (next to other parse locals):

```csharp
(double, double, double, double, bool)? truthCal = null;
string? ransacSeedsCsv = null;
bool traceConsole = false;
string? otlpEndpoint = null;
```

Add the switch cases (inside the main `for (int i...)` switch, before `case "-h"`):

```csharp
case "--truth-cal":
    truthCal = ParseSeed(Next(argv, ref i)); // same format: scale,rot,ox,oy,mirror
    break;
case "--ransac-seeds-csv":
    ransacSeedsCsv = Next(argv, ref i);
    break;
case "--trace-console":
    traceConsole = true;
    break;
case "--otlp":
    otlpEndpoint = Next(argv, ref i);
    break;
```

Reorder `ParseSeed` so it accepts `scale,rot,ox,oy,mirror` (the existing one takes `rot,scale,ox,oy,mirror`). Add a new helper that swaps the order, named `ParseTruthCal`:

```csharp
private static (double, double, double, double, bool) ParseTruthCal(string s)
{
    var parts = s.Split(',', 5);
    if (parts.Length != 5) throw new UserFacingException($"--truth-cal wants 'scale,rot,ox,oy,mirror' (got '{s}')");
    return (
        double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
        double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
        double.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
        double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture),
        bool.Parse(parts[4].Trim()));
}
```

…and call `ParseTruthCal` (not `ParseSeed`) in the `--truth-cal` case.

Pass the four new fields through to the `CliArgs` constructor at the bottom of `Parse`. Add them in the same positional order to the record header. Update help text to document the four flags under a "synthesis-probe diagnostic" section:

```
synthesis-probe diagnostic (--phase synthesis-probe):
  --truth-cal <scale,rot,ox,oy,mirror>  known-correct calibration for E1-E5 (mirror = true|false)
  --ransac-seeds-csv <path>             CSV of candidate calibrations to score (E4):
                                          label,scale,rot,ox,oy,mirror
  --trace-console                       emit OTel spans to stdout
  --otlp <endpoint>                     emit OTel spans to the named OTLP endpoint
```

- [ ] **Step 4: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~CliArgsSynthesisProbeTests"
```

Expected: PASS — 4 tests.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/CliArgs.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/CliArgsSynthesisProbeTests.cs
git commit -m "feat(synthesis-probe): parse --truth-cal, --ransac-seeds-csv, --trace-console, --otlp"
```

---

### Task 4: IconLikelihoodField.Build (deviation + alpha-masked NCC)

**Files:**
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/IconLikelihoodField.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/IconLikelihoodFieldBuildTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// IconLikelihoodFieldBuildTests.cs
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class IconLikelihoodFieldBuildTests
{
    [Fact]
    public void Field_peaks_at_known_icon_location()
    {
        // 64x64 background of gray 128.
        const int W = 64, H = 64;
        var screenshot = NewGray(W, H, fill: 128);
        var baseTex = NewGray(W, H, fill: 128);

        // 5x5 template: bright cross. Fully opaque alpha.
        var template = new IconTemplate(
            Name: "x",
            LandmarkType: "Portal",
            PivotX: 0.5,
            PivotY: 0.5,
            Width: 5,
            Height: 5,
            Pixels: new byte[] { 0,0,255,0,0, 0,0,255,0,0, 255,255,255,255,255, 0,0,255,0,0, 0,0,255,0,0 },
            Alpha:  new byte[] { 0,0,255,0,0, 0,0,255,0,0, 255,255,255,255,255, 0,0,255,0,0, 0,0,255,0,0 });

        // Stamp the template on the screenshot at center (32,32).
        StampTemplate(screenshot, template, cx: 32, cy: 32);

        var field = IconLikelihoodField.Build(screenshot, baseTex, template);

        field.GetLength(0).Should().Be(H);
        field.GetLength(1).Should().Be(W);
        var (maxX, maxY) = Argmax(field);
        maxX.Should().BeInRange(31, 33); // centered on the stamp, allowing 1 px tolerance
        maxY.Should().BeInRange(31, 33);
        field[maxY, maxX].Should().BeGreaterThan(0.8); // strong NCC on a clean stamp
    }

    private static GrayImage NewGray(int w, int h, byte fill)
    {
        var p = new byte[w * h];
        Array.Fill(p, fill);
        return new GrayImage(w, h, p);
    }

    private static void StampTemplate(GrayImage img, IconTemplate t, int cx, int cy)
    {
        int x0 = cx - t.Width / 2;
        int y0 = cy - t.Height / 2;
        for (int ty = 0; ty < t.Height; ty++)
            for (int tx = 0; tx < t.Width; tx++)
            {
                if (t.Alpha[ty * t.Width + tx] == 0) continue;
                int x = x0 + tx, y = y0 + ty;
                if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) continue;
                img.Pixels[y * img.Width + x] = t.Pixels[ty * t.Width + tx];
            }
    }

    private static (int X, int Y) Argmax(double[,] field)
    {
        int bestX = 0, bestY = 0;
        double bestV = double.NegativeInfinity;
        for (int y = 0; y < field.GetLength(0); y++)
            for (int x = 0; x < field.GetLength(1); x++)
                if (field[y, x] > bestV) { bestV = field[y, x]; bestX = x; bestY = y; }
        return (bestX, bestY);
    }
}
```

> **Note:** `IconTemplate` lives at `src/Mithril.MapCalibration/Detection/IconTemplate.cs`. If its constructor signature differs from the above, mirror its actual shape — the synthetic stamp logic stays the same.

- [ ] **Step 2: Run, verify it fails**

Expected: BUILD FAILURE — `IconLikelihoodField` not found.

- [ ] **Step 3: Implement**

```csharp
// IconLikelihoodField.cs
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class IconLikelihoodField
{
    public static double[,] Build(GrayImage screenshot, GrayImage alignedBase, IconTemplate template)
    {
        if (screenshot.Width != alignedBase.Width || screenshot.Height != alignedBase.Height)
            throw new ArgumentException("screenshot and aligned base must have matching dimensions");

        int w = screenshot.Width, h = screenshot.Height;
        var deviation = new byte[w * h];
        for (int i = 0; i < deviation.Length; i++)
        {
            int d = screenshot.Pixels[i] - alignedBase.Pixels[i];
            deviation[i] = d > 0 ? (byte)Math.Min(255, d) : (byte)0;
        }
        var devImage = new GrayImage(w, h, deviation);

        return ScoreAll(devImage, template);
    }

    /// <summary>
    /// Whole-image alpha-masked NCC. Returns an HxW array of raw NCC scores
    /// (no NMS, no thresholding). For each anchor (cx, cy), correlates the
    /// alpha-opaque pixels of <paramref name="template"/> against the image
    /// window centered on the template's pivot. Scores at positions where the
    /// template would overhang the image edge are 0.
    /// </summary>
    public static double[,] ScoreAll(GrayImage image, IconTemplate template)
    {
        int W = image.Width, H = image.Height;
        int tw = template.Width, th = template.Height;
        int ax = (int)Math.Round(template.PivotX * tw);
        int ay = (int)Math.Round(template.PivotY * th);

        // Precompute template mean / variance over opaque pixels only.
        double tSum = 0;
        int opaqueCount = 0;
        for (int i = 0; i < tw * th; i++)
        {
            if (template.Alpha[i] == 0) continue;
            tSum += template.Pixels[i];
            opaqueCount++;
        }
        if (opaqueCount == 0) return new double[H, W];
        double tMean = tSum / opaqueCount;
        double tVar = 0;
        for (int i = 0; i < tw * th; i++)
        {
            if (template.Alpha[i] == 0) continue;
            double d = template.Pixels[i] - tMean;
            tVar += d * d;
        }
        double tStd = Math.Sqrt(tVar);
        if (tStd < 1e-9) return new double[H, W];

        var field = new double[H, W];
        Parallel.For(0, H, cy =>
        {
            int y0 = cy - ay;
            if (y0 < 0 || y0 + th > H) return;
            for (int cx = 0; cx < W; cx++)
            {
                int x0 = cx - ax;
                if (x0 < 0 || x0 + tw > W) continue;

                double iSum = 0;
                for (int ty = 0; ty < th; ty++)
                    for (int tx = 0; tx < tw; tx++)
                        if (template.Alpha[ty * tw + tx] != 0)
                            iSum += image.Pixels[(y0 + ty) * W + (x0 + tx)];

                double iMean = iSum / opaqueCount;
                double iVar = 0, cov = 0;
                for (int ty = 0; ty < th; ty++)
                    for (int tx = 0; tx < tw; tx++)
                    {
                        if (template.Alpha[ty * tw + tx] == 0) continue;
                        double di = image.Pixels[(y0 + ty) * W + (x0 + tx)] - iMean;
                        double dt = template.Pixels[ty * tw + tx] - tMean;
                        iVar += di * di;
                        cov += di * dt;
                    }
                double iStd = Math.Sqrt(iVar);
                field[cy, cx] = (iStd < 1e-9) ? 0.0 : cov / (iStd * tStd);
            }
        });
        return field;
    }
}
```

- [ ] **Step 4: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~IconLikelihoodFieldBuildTests"
```

Expected: PASS — argmax within tolerance, NCC ≥ 0.8 at peak.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/IconLikelihoodField.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/IconLikelihoodFieldBuildTests.cs
git commit -m "feat(synthesis-probe): build per-type icon-likelihood field from screenshot deviation"
```

---

### Task 5: IconLikelihoodField.Sample (bicubic)

**Files:**
- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/IconLikelihoodField.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/IconLikelihoodFieldSampleTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// IconLikelihoodFieldSampleTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class IconLikelihoodFieldSampleTests
{
    [Fact]
    public void Sample_at_integer_position_returns_grid_value()
    {
        var field = new double[3, 3];
        field[1, 1] = 0.7;
        IconLikelihoodField.Sample(field, 1.0, 1.0).Should().BeApproximately(0.7, 1e-9);
    }

    [Fact]
    public void Sample_between_grid_points_interpolates_monotonically()
    {
        // Linearly-rising field along x: f(x,y) = x*0.1.
        var field = new double[3, 5];
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 5; x++)
                field[y, x] = x * 0.1;

        var s1 = IconLikelihoodField.Sample(field, 1.0, 1.0);
        var s15 = IconLikelihoodField.Sample(field, 1.5, 1.0);
        var s2 = IconLikelihoodField.Sample(field, 2.0, 1.0);

        s15.Should().BeGreaterThan(s1);
        s15.Should().BeLessThan(s2);
        s15.Should().BeApproximately(0.15, 0.02);  // bicubic stays close to linear on a linear field
    }

    [Fact]
    public void Sample_outside_field_returns_zero()
    {
        var field = new double[3, 3];
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) field[y, x] = 1.0;

        IconLikelihoodField.Sample(field, -1.0, 1.0).Should().Be(0.0);
        IconLikelihoodField.Sample(field, 3.5, 1.0).Should().Be(0.0);
        IconLikelihoodField.Sample(field, 1.0, -0.5).Should().Be(0.0);
    }
}
```

- [ ] **Step 2: Run, verify it fails**

Expected: BUILD FAILURE — `Sample` method not found.

- [ ] **Step 3: Implement**

In `IconLikelihoodField.cs`, add:

```csharp
public static double Sample(double[,] field, double x, double y)
{
    int h = field.GetLength(0), w = field.GetLength(1);
    if (x < 0 || y < 0 || x > w - 1 || y > h - 1) return 0.0;

    int ix = (int)Math.Floor(x);
    int iy = (int)Math.Floor(y);
    double fx = x - ix;
    double fy = y - iy;

    // Cubic Hermite (Catmull-Rom-ish) over 4 samples per row, then over 4 row results.
    double[] col = new double[4];
    for (int j = -1; j <= 2; j++)
    {
        int yy = Math.Clamp(iy + j, 0, h - 1);
        double[] row = new double[4];
        for (int i = -1; i <= 2; i++)
        {
            int xx = Math.Clamp(ix + i, 0, w - 1);
            row[i + 1] = field[yy, xx];
        }
        col[j + 1] = CubicHermite(row[0], row[1], row[2], row[3], fx);
    }
    return CubicHermite(col[0], col[1], col[2], col[3], fy);
}

private static double CubicHermite(double a, double b, double c, double d, double t)
{
    double a0 = -0.5 * a + 1.5 * b - 1.5 * c + 0.5 * d;
    double a1 = a - 2.5 * b + 2.0 * c - 0.5 * d;
    double a2 = -0.5 * a + 0.5 * c;
    double a3 = b;
    return ((a0 * t + a1) * t + a2) * t + a3;
}
```

- [ ] **Step 4: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~IconLikelihoodFieldSampleTests"
```

Expected: PASS — 3 tests.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/IconLikelihoodField.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/IconLikelihoodFieldSampleTests.cs
git commit -m "feat(synthesis-probe): bicubic Sample over the icon-likelihood field"
```

---

### Task 6: JEvaluator

**Files:**
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/ReferencePoint.cs`
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/JEvaluator.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/JEvaluatorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// JEvaluatorTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class JEvaluatorTests
{
    [Fact]
    public void J_is_high_when_refs_project_onto_field_peaks()
    {
        // Two 64x64 fields with one peak each at known crop pixels.
        var portalField = ZerosWithPeak(64, 64, peakX: 20, peakY: 20, value: 0.95);
        var npcField = ZerosWithPeak(64, 64, peakX: 50, peakY: 40, value: 0.92);
        var fields = new Dictionary<string, double[,]>
        {
            ["Portal"] = portalField,
            ["Npc"] = npcField,
        };

        // Two refs in world coords that land at the peaks under identity-ish transform.
        var refs = new[]
        {
            new ReferencePoint("p1", "Portal", WorldX: 0, WorldZ: 0),
            new ReferencePoint("n1", "Npc", WorldX: 30, WorldZ: -20),
        };

        // Transform: identity-ish, picking origin so world (0,0) lands at (20,20)
        // and (30,-20) lands at (50,40). Scale = 1 px/unit, no rotation, no mirror.
        var truth = new CandidateTransform(Scale: 1.0, RotRadians: 0.0, Mirror: false, Tx: 20.0, Ty: 20.0);

        var jTruth = JEvaluator.Evaluate(truth, fields, refs);
        jTruth.J.Should().BeGreaterThan(1.8); // sum of two ~0.9 peaks
        jTruth.RefsAboveHalf.Should().Be(2);
        jTruth.RefsOffCrop.Should().Be(0);

        // Shift origin so refs land far off the peaks.
        var wrong = truth with { Tx = -100.0, Ty = -100.0 };
        var jWrong = JEvaluator.Evaluate(wrong, fields, refs);
        jWrong.J.Should().BeLessThan(0.1);
        jWrong.RefsOffCrop.Should().Be(2);
    }

    private static double[,] ZerosWithPeak(int w, int h, int peakX, int peakY, double value)
    {
        var f = new double[h, w];
        f[peakY, peakX] = value;
        return f;
    }
}
```

- [ ] **Step 2: Run, verify fails**

Expected: BUILD FAILURE — `JEvaluator`, `ReferencePoint`, `JResult` not found.

- [ ] **Step 3: Implement ReferencePoint**

```csharp
// ReferencePoint.cs
namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal readonly record struct ReferencePoint(string Label, string LandmarkType, double WorldX, double WorldZ);
```

- [ ] **Step 4: Implement JEvaluator**

```csharp
// JEvaluator.cs
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal readonly record struct JResult(double J, int RefsAboveHalf, int RefsOffCrop, IReadOnlyList<double> PerRefScores);

internal static class JEvaluator
{
    public static JResult Evaluate(
        CandidateTransform t,
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<ReferencePoint> refs)
    {
        double j = 0;
        int aboveHalf = 0;
        int offCrop = 0;
        var perRef = new double[refs.Count];

        for (int i = 0; i < refs.Count; i++)
        {
            var r = refs[i];
            if (!fieldsByType.TryGetValue(r.LandmarkType, out var field))
            {
                perRef[i] = 0;
                continue;
            }
            var p = t.Apply(new WorldCoord(r.WorldX, 0, r.WorldZ));
            int h = field.GetLength(0), w = field.GetLength(1);
            if (p.X < 0 || p.Y < 0 || p.X > w - 1 || p.Y > h - 1)
            {
                offCrop++;
                perRef[i] = 0;
                continue;
            }
            var score = IconLikelihoodField.Sample(field, p.X, p.Y);
            perRef[i] = score;
            j += score;
            if (score >= 0.5) aboveHalf++;
        }

        return new JResult(j, aboveHalf, offCrop, perRef);
    }
}
```

- [ ] **Step 5: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~JEvaluatorTests"
```

Expected: PASS — 1 test.

- [ ] **Step 6: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/ReferencePoint.cs tools/MapCalibrationFromScreenshot/SynthesisProbe/JEvaluator.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/JEvaluatorTests.cs
git commit -m "feat(synthesis-probe): JEvaluator sums per-ref field samples"
```

---

### Task 7: SynthesisProbeWriter (CSV + field PNG + landscape PNG)

**Files:**
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbeWriter.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/SynthesisProbeWriterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// SynthesisProbeWriterTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class SynthesisProbeWriterTests
{
    [Fact]
    public void Csv_writes_header_and_row()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-csv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var w = new SynthesisProbeWriter(dir))
            {
                var t = new CandidateTransform(0.5, 0.0, false, 100, 200);
                var jr = new JResult(J: 1.7, RefsAboveHalf: 2, RefsOffCrop: 0, PerRefScores: new[] { 0.9, 0.8 });
                w.AppendCsvRow("E1", "truth", t, jr, dominanceVsRunnerUp: double.NaN);
            }
            var lines = File.ReadAllLines(Path.Combine(dir, "synthesis_probe.csv"));
            lines[0].Should().Be("experiment,label,scale,rot,mirror,tx,ty,J,refs_above_0.5,dominance_vs_runner_up");
            lines[1].Should().StartWith("E1,truth,0.5,0,false,100,200,1.7,2,");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Field_png_written_with_expected_dims()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-png-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var w = new SynthesisProbeWriter(dir);
            var field = new double[10, 20];
            field[5, 10] = 0.9;
            w.WriteFieldPng("Portal", field);
            File.Exists(Path.Combine(dir, "field_Portal.png")).Should().BeTrue();
            // Just check it's a valid PNG by re-reading dimensions via System.Drawing.
            using var img = System.Drawing.Image.FromFile(Path.Combine(dir, "field_Portal.png"));
            img.Width.Should().Be(20);
            img.Height.Should().Be(10);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Landscape_png_dimensions_match_input()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-land-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var w = new SynthesisProbeWriter(dir);
            var landscape = new double[65, 65];
            w.WriteLandscapePng("translation", landscape);
            using var img = System.Drawing.Image.FromFile(Path.Combine(dir, "grid_landscape_translation.png"));
            img.Width.Should().Be(65);
            img.Height.Should().Be(65);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run, verify fails**

Expected: BUILD FAILURE — `SynthesisProbeWriter` not found.

- [ ] **Step 3: Implement**

```csharp
// SynthesisProbeWriter.cs
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal sealed class SynthesisProbeWriter : IDisposable
{
    private readonly StreamWriter _csv;
    private readonly string _outDir;

    public SynthesisProbeWriter(string outDir)
    {
        Directory.CreateDirectory(outDir);
        _outDir = outDir;
        _csv = new StreamWriter(Path.Combine(outDir, "synthesis_probe.csv"));
        _csv.WriteLine("experiment,label,scale,rot,mirror,tx,ty,J,refs_above_0.5,dominance_vs_runner_up");
    }

    public void AppendCsvRow(string experiment, string label, CandidateTransform t, JResult jr, double dominanceVsRunnerUp)
    {
        _csv.Write(experiment); _csv.Write(',');
        _csv.Write(label); _csv.Write(',');
        _csv.Write(t.Scale.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.Write(t.RotRadians.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.Write(t.Mirror ? "true" : "false"); _csv.Write(',');
        _csv.Write(t.Tx.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.Write(t.Ty.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.Write(jr.J.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.Write(jr.RefsAboveHalf.ToString(CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.WriteLine(double.IsNaN(dominanceVsRunnerUp) ? "" : dominanceVsRunnerUp.ToString("R", CultureInfo.InvariantCulture));
    }

    public void WriteFieldPng(string type, double[,] field) =>
        WriteScalarPng(Path.Combine(_outDir, $"field_{type}.png"), field, vmin: -1.0, vmax: 1.0);

    public void WriteLandscapePng(string label, double[,] landscape) =>
        WriteScalarPng(Path.Combine(_outDir, $"grid_landscape_{label}.png"), landscape, vmin: Min(landscape), vmax: Max(landscape));

    private static void WriteScalarPng(string path, double[,] field, double vmin, double vmax)
    {
        int h = field.GetLength(0), w = field.GetLength(1);
        double span = (vmax - vmin) > 1e-9 ? (vmax - vmin) : 1.0;
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            byte[] row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double v = (field[y, x] - vmin) / span;
                    v = Math.Clamp(v, 0, 1);
                    byte g = (byte)Math.Round(v * 255);
                    row[x * 3 + 0] = g;
                    row[x * 3 + 1] = g;
                    row[x * 3 + 2] = g;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * stride, stride);
            }
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }

    private static double Min(double[,] f)
    {
        double m = double.PositiveInfinity;
        foreach (var v in f) if (v < m) m = v;
        return m;
    }

    private static double Max(double[,] f)
    {
        double m = double.NegativeInfinity;
        foreach (var v in f) if (v > m) m = v;
        return m;
    }

    public void Dispose() => _csv.Dispose();
}
```

- [ ] **Step 4: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~SynthesisProbeWriterTests"
```

Expected: PASS — 3 tests.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbeWriter.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/SynthesisProbeWriterTests.cs
git commit -m "feat(synthesis-probe): CSV + field PNG + landscape PNG writers"
```

---

### Task 8: SynthesisProbeTracer (OTel ActivitySource + exporters)

**Files:**
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbeTracer.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/SynthesisProbeTracerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// SynthesisProbeTracerTests.cs
using System.Diagnostics;
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class SynthesisProbeTracerTests
{
    [Fact]
    public void ActivitySource_emits_span_when_listener_is_attached()
    {
        var emitted = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == SynthesisProbeTracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = emitted.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using (var act = SynthesisProbeTracer.Source.StartActivity("test.span"))
        {
            act?.SetTag("foo", "bar");
        }

        emitted.Should().ContainSingle(a => a.OperationName == "test.span");
        emitted[0].Tags.Should().Contain(kv => kv.Key == "foo" && kv.Value == "bar");
    }

    [Fact]
    public void No_exception_when_no_listener_attached()
    {
        var act = SynthesisProbeTracer.Source.StartActivity("never.listened");
        act.Should().BeNull(); // no listener → null span, no NPE on access via `?.`
    }
}
```

- [ ] **Step 2: Run, verify fails**

Expected: BUILD FAILURE — `SynthesisProbeTracer` not found.

- [ ] **Step 3: Implement**

```csharp
// SynthesisProbeTracer.cs
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class SynthesisProbeTracer
{
    public const string ActivitySourceName = "Mithril.Tools.MapCalibrationSynthesisProbe";
    public static readonly ActivitySource Source = new(ActivitySourceName);

    public static TracerProvider? Configure(bool traceConsole, string? otlpEndpoint)
    {
        if (!traceConsole && otlpEndpoint is null) return null;

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("MapCalibrationSynthesisProbe"))
            .AddSource(ActivitySourceName);

        if (traceConsole) builder.AddConsoleExporter();
        if (otlpEndpoint is not null)
            builder.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));

        return builder.Build();
    }
}
```

- [ ] **Step 4: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~SynthesisProbeTracerTests"
```

Expected: PASS — 2 tests.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbeTracer.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/SynthesisProbeTracerTests.cs
git commit -m "feat(synthesis-probe): OTel ActivitySource + console/OTLP exporter selection"
```

---

### Task 9: E1 (truth score) + E2 (translation sweep) + E3 (scale sweep)

These three experiments all share the same fixture and writer. Grouping them keeps the plan compact without losing TDD discipline — each gets its own test, each fails before its impl lands.

**Files:**
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E1_TruthScore.cs`
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E2_TranslationSweep.cs`
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E3_ScaleSweep.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Experiments/E1_E2_E3_Tests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Experiments/E1_E2_E3_Tests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Experiments;

public class E1_E2_E3_Tests
{
    private static (IReadOnlyDictionary<string, double[,]> fields, IReadOnlyList<ReferencePoint> refs, CandidateTransform truth) SyntheticScene()
    {
        // 64x64 portal field with one strong peak at (20,20).
        var portal = new double[64, 64];
        portal[20, 20] = 0.9;
        var refs = new[] { new ReferencePoint("p1", "Portal", WorldX: 0, WorldZ: 0) };
        var truth = new CandidateTransform(Scale: 1.0, RotRadians: 0.0, Mirror: false, Tx: 20.0, Ty: 20.0);
        return (new Dictionary<string, double[,]> { ["Portal"] = portal }, refs, truth);
    }

    [Fact]
    public void E1_writes_truth_row()
    {
        var (fields, refs, truth) = SyntheticScene();
        var dir = NewTempDir();
        try
        {
            using (var w = new SynthesisProbeWriter(dir))
                E1_TruthScore.Run(fields, refs, truth, w);
            var rows = File.ReadAllLines(Path.Combine(dir, "synthesis_probe.csv"));
            rows.Should().Contain(r => r.StartsWith("E1,truth,"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void E2_writes_landscape_with_peak_at_truth_center()
    {
        var (fields, refs, truth) = SyntheticScene();
        var dir = NewTempDir();
        try
        {
            using (var w = new SynthesisProbeWriter(dir))
                E2_TranslationSweep.Run(fields, refs, truth, templateSizePx: 5, w);
            var rows = File.ReadAllLines(Path.Combine(dir, "synthesis_probe.csv"));
            rows.Where(r => r.StartsWith("E2,")).Should().NotBeEmpty();
            File.Exists(Path.Combine(dir, "grid_landscape_translation.png")).Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void E3_writes_51_scale_rows()
    {
        var (fields, refs, truth) = SyntheticScene();
        var dir = NewTempDir();
        try
        {
            using (var w = new SynthesisProbeWriter(dir))
                E3_ScaleSweep.Run(fields, refs, truth, w);
            var rows = File.ReadAllLines(Path.Combine(dir, "synthesis_probe.csv"));
            rows.Count(r => r.StartsWith("E3,")).Should().Be(51);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-e123-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
```

- [ ] **Step 2: Run, verify fails**

Expected: BUILD FAILURE — `E1_TruthScore`, `E2_TranslationSweep`, `E3_ScaleSweep` not found.

- [ ] **Step 3: Implement E1**

```csharp
// Experiments/E1_TruthScore.cs
namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

internal static class E1_TruthScore
{
    public static JResult Run(
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<ReferencePoint> refs,
        CandidateTransform truth,
        SynthesisProbeWriter writer)
    {
        using var act = SynthesisProbeTracer.Source.StartActivity("experiment.E1");
        var jr = JEvaluator.Evaluate(truth, fieldsByType, refs);
        act?.SetTag("eval_count", 1);
        act?.SetTag("J_truth", jr.J);
        act?.SetTag("refs_above_0.5", jr.RefsAboveHalf);
        act?.SetTag("refs_off_crop", jr.RefsOffCrop);
        writer.AppendCsvRow("E1", "truth", truth, jr, dominanceVsRunnerUp: double.NaN);
        return jr;
    }
}
```

- [ ] **Step 4: Implement E2**

```csharp
// Experiments/E2_TranslationSweep.cs
namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

internal static class E2_TranslationSweep
{
    public static void Run(
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<ReferencePoint> refs,
        CandidateTransform truth,
        int templateSizePx,
        SynthesisProbeWriter writer)
    {
        using var act = SynthesisProbeTracer.Source.StartActivity("experiment.E2");
        int halfWindow = 2 * templateSizePx;
        int side = halfWindow * 2 + 1;
        var landscape = new double[side, side];

        double jBest = double.NegativeInfinity;
        int evals = 0;
        for (int dy = -halfWindow; dy <= halfWindow; dy++)
            for (int dx = -halfWindow; dx <= halfWindow; dx++)
            {
                var t = truth with { Tx = truth.Tx + dx, Ty = truth.Ty + dy };
                var jr = JEvaluator.Evaluate(t, fieldsByType, refs);
                landscape[dy + halfWindow, dx + halfWindow] = jr.J;
                if (jr.J > jBest) jBest = jr.J;
                evals++;
                if (evals % 100 == 0)
                    writer.AppendCsvRow("E2", $"dx={dx},dy={dy}", t, jr, dominanceVsRunnerUp: double.NaN);
            }
        writer.WriteLandscapePng("translation", landscape);

        act?.SetTag("eval_count", evals);
        act?.SetTag("J_best", jBest);
        act?.SetTag("window_px", halfWindow);
    }
}
```

> **Note:** every 100th row is written to keep the CSV readable; the full landscape lives in the PNG. Adjust the stride if you want denser CSV coverage.

- [ ] **Step 5: Implement E3**

```csharp
// Experiments/E3_ScaleSweep.cs
namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

internal static class E3_ScaleSweep
{
    public static void Run(
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<ReferencePoint> refs,
        CandidateTransform truth,
        SynthesisProbeWriter writer)
    {
        using var act = SynthesisProbeTracer.Source.StartActivity("experiment.E3");
        double jBest = double.NegativeInfinity;
        int evals = 0;
        // -25% .. +25% in 1% steps = 51 samples.
        for (int pct = -25; pct <= 25; pct++)
        {
            double factor = 1.0 + pct / 100.0;
            var t = truth with { Scale = truth.Scale * factor };
            var jr = JEvaluator.Evaluate(t, fieldsByType, refs);
            writer.AppendCsvRow("E3", $"pct={pct}", t, jr, dominanceVsRunnerUp: double.NaN);
            if (jr.J > jBest) jBest = jr.J;
            evals++;
        }
        act?.SetTag("eval_count", evals);
        act?.SetTag("J_best", jBest);
    }
}
```

- [ ] **Step 6: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~E1_E2_E3_Tests"
```

Expected: PASS — 3 tests.

- [ ] **Step 7: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/ tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Experiments/
git commit -m "feat(synthesis-probe): experiments E1 (truth), E2 (translation sweep), E3 (scale sweep)"
```

---

### Task 10: RansacSeedsCsv reader + E4 (RANSAC seed score)

**Files:**
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/RansacSeedsCsv.cs`
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E4_RansacSeedScore.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Experiments/E4_RansacSeedScoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Experiments/E4_RansacSeedScoreTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Experiments;

public class E4_RansacSeedScoreTests
{
    [Fact]
    public void Reads_csv_and_scores_each_seed_with_dominance()
    {
        var portal = new double[64, 64];
        portal[20, 20] = 0.9;
        var fields = new Dictionary<string, double[,]> { ["Portal"] = portal };
        var refs = new[] { new ReferencePoint("p1", "Portal", 0, 0) };
        var truth = new CandidateTransform(1.0, 0.0, false, 20, 20);

        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-e4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var csv = Path.Combine(dir, "seeds.csv");
            File.WriteAllLines(csv, new[]
            {
                "label,scale,rot,ox,oy,mirror",
                "near_truth,1.0,0.0,21,21,false",
                "far_off,1.0,0.0,-100,-100,false",
            });

            using (var w = new SynthesisProbeWriter(dir))
                E4_RansacSeedScore.Run(fields, refs, truth, csv, w);

            var rows = File.ReadAllLines(Path.Combine(dir, "synthesis_probe.csv")).Where(r => r.StartsWith("E4,")).ToList();
            rows.Should().HaveCount(2);
            // The "near_truth" row scores nonzero; "far_off" scores ~0.
            var nearRow = rows.Single(r => r.Contains(",near_truth,"));
            var farRow = rows.Single(r => r.Contains(",far_off,"));
            // J column is the 8th comma-separated (idx 7).
            double JOf(string row) => double.Parse(row.Split(',')[7], System.Globalization.CultureInfo.InvariantCulture);
            JOf(nearRow).Should().BeGreaterThan(JOf(farRow));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run, verify fails**

Expected: BUILD FAILURE — `RansacSeedsCsv`, `E4_RansacSeedScore` not found.

- [ ] **Step 3: Implement RansacSeedsCsv**

```csharp
// RansacSeedsCsv.cs
using System.Globalization;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class RansacSeedsCsv
{
    public static List<(string Label, CandidateTransform T)> Read(string path)
    {
        var rows = new List<(string, CandidateTransform)>();
        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',', 6);
            if (parts.Length != 6) throw new FormatException($"bad row '{line}' (want label,scale,rot,ox,oy,mirror)");
            rows.Add((parts[0],
                new CandidateTransform(
                    double.Parse(parts[1], CultureInfo.InvariantCulture),
                    double.Parse(parts[2], CultureInfo.InvariantCulture),
                    bool.Parse(parts[5]),
                    double.Parse(parts[3], CultureInfo.InvariantCulture),
                    double.Parse(parts[4], CultureInfo.InvariantCulture))));
        }
        return rows;
    }
}
```

- [ ] **Step 4: Implement E4**

```csharp
// Experiments/E4_RansacSeedScore.cs
namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

internal static class E4_RansacSeedScore
{
    public static void Run(
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<ReferencePoint> refs,
        CandidateTransform truth,
        string csvPath,
        SynthesisProbeWriter writer)
    {
        using var act = SynthesisProbeTracer.Source.StartActivity("experiment.E4");
        var seeds = RansacSeedsCsv.Read(csvPath);
        var jTruth = JEvaluator.Evaluate(truth, fieldsByType, refs);
        double jMaxSeed = double.NegativeInfinity;
        foreach (var (_, t) in seeds)
        {
            var jr = JEvaluator.Evaluate(t, fieldsByType, refs);
            if (jr.J > jMaxSeed) jMaxSeed = jr.J;
        }
        foreach (var (label, t) in seeds)
        {
            var jr = JEvaluator.Evaluate(t, fieldsByType, refs);
            double dominance = jMaxSeed > 0 ? jr.J / jMaxSeed : double.NaN;
            writer.AppendCsvRow("E4", label, t, jr, dominance);
        }
        act?.SetTag("eval_count", seeds.Count);
        act?.SetTag("J_truth", jTruth.J);
        act?.SetTag("J_max_seed", jMaxSeed);
        act?.SetTag("dominance", jMaxSeed > 0 ? jTruth.J / jMaxSeed : double.NaN);
    }
}
```

- [ ] **Step 5: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~E4_RansacSeedScoreTests"
```

Expected: PASS — 1 test.

- [ ] **Step 6: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/RansacSeedsCsv.cs tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E4_RansacSeedScore.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Experiments/E4_RansacSeedScoreTests.cs
git commit -m "feat(synthesis-probe): E4 scores --ransac-seeds-csv candidates against truth"
```

---

### Task 11: LocalRefine (gradient ascent on bicubic field)

**Files:**
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/LocalRefine.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/LocalRefineTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// LocalRefineTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class LocalRefineTests
{
    [Fact]
    public void Pulls_in_from_5px_offset_on_gaussian_peak()
    {
        // 64x64 field with a smooth gaussian peak at (32,32), sigma=3.
        int W = 64, H = 64;
        var f = new double[H, W];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double dx = x - 32, dy = y - 32;
                f[y, x] = Math.Exp(-(dx * dx + dy * dy) / (2 * 3.0 * 3.0));
            }
        var fields = new Dictionary<string, double[,]> { ["Portal"] = f };
        var refs = new[] { new ReferencePoint("p1", "Portal", 0, 0) };
        var seed = new CandidateTransform(Scale: 1.0, RotRadians: 0.0, Mirror: false, Tx: 27.0, Ty: 32.0); // 5 px off in x

        var refined = LocalRefine.Run(seed, fields, refs, maxIter: 60, stepInit: 1.0);

        refined.Tx.Should().BeApproximately(32.0, 0.5);
        refined.Ty.Should().BeApproximately(32.0, 0.5);
    }
}
```

- [ ] **Step 2: Run, verify fails**

Expected: BUILD FAILURE — `LocalRefine` not found.

- [ ] **Step 3: Implement (coordinate-descent ascent on Tx/Ty/Scale)**

```csharp
// LocalRefine.cs
namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class LocalRefine
{
    /// <summary>
    /// Hill-climbing ascent on (Tx, Ty, Scale). Uses adaptive step-halving:
    /// at each iteration tries +step in each axis, takes the move with the
    /// best J; halves the step when no axis improves. Holds Rot and Mirror fixed
    /// (those are discrete branches at the grid level).
    /// </summary>
    public static CandidateTransform Run(
        CandidateTransform seed,
        IReadOnlyDictionary<string, double[,]> fields,
        IReadOnlyList<ReferencePoint> refs,
        int maxIter,
        double stepInit)
    {
        var t = seed;
        double bestJ = JEvaluator.Evaluate(t, fields, refs).J;
        double stepXY = stepInit;
        double stepScale = stepInit * Math.Max(1e-6, seed.Scale) * 0.01;

        for (int iter = 0; iter < maxIter; iter++)
        {
            var candidates = new (CandidateTransform T, double J)[]
            {
                (t with { Tx = t.Tx + stepXY }, 0),
                (t with { Tx = t.Tx - stepXY }, 0),
                (t with { Ty = t.Ty + stepXY }, 0),
                (t with { Ty = t.Ty - stepXY }, 0),
                (t with { Scale = t.Scale + stepScale }, 0),
                (t with { Scale = Math.Max(1e-6, t.Scale - stepScale) }, 0),
            };
            int bestI = -1;
            double newBest = bestJ;
            for (int i = 0; i < candidates.Length; i++)
            {
                var j = JEvaluator.Evaluate(candidates[i].T, fields, refs).J;
                candidates[i] = (candidates[i].T, j);
                if (j > newBest) { newBest = j; bestI = i; }
            }
            if (bestI < 0)
            {
                stepXY *= 0.5;
                stepScale *= 0.5;
                if (stepXY < 0.01 && stepScale < 1e-6) break;
            }
            else
            {
                t = candidates[bestI].T;
                bestJ = newBest;
            }
        }
        return t;
    }
}
```

- [ ] **Step 4: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~LocalRefineTests"
```

Expected: PASS — refined Tx within 0.5 px of 32.0.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/LocalRefine.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/LocalRefineTests.cs
git commit -m "feat(synthesis-probe): LocalRefine hill-climbing ascent on (Tx, Ty, Scale)"
```

---

### Task 12: E5 (cold grid + top-8 refine)

**Files:**
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E5_ColdGrid.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Experiments/E5_ColdGridTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Experiments/E5_ColdGridTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Experiments;

public class E5_ColdGridTests
{
    [Fact]
    public void Top8_includes_a_near_truth_entry_after_refine()
    {
        // 128x128 portal field with a sharp peak at (64,64).
        var f = new double[128, 128];
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                double dx = x - 64, dy = y - 64;
                f[y, x] = Math.Exp(-(dx * dx + dy * dy) / (2 * 3.0 * 3.0));
            }
        var fields = new Dictionary<string, double[,]> { ["Portal"] = f };
        var refs = new[] { new ReferencePoint("p1", "Portal", 0, 0) };
        var truth = new CandidateTransform(1.0, 0.0, false, 64, 64);

        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-e5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var w = new SynthesisProbeWriter(dir);
            var report = E5_ColdGrid.Run(
                fields, refs, truth,
                scaleBracket: (0.5, 2.0),
                scaleSamples: 8,
                cropWidth: 128, cropHeight: 128,
                gridStepPx: 8,
                templateSizePx: 5,
                writer: w);
            report.BestDistanceToTruthPx.Should().BeLessThan(5.0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run, verify fails**

Expected: BUILD FAILURE — `E5_ColdGrid` not found.

- [ ] **Step 3: Implement**

```csharp
// Experiments/E5_ColdGrid.cs
namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

internal sealed record E5Report(double BestDistanceToTruthPx, double JBestAfterRefine, IReadOnlyList<(CandidateTransform T, double J, double DistanceToTruth)> Top8AfterRefine);

internal static class E5_ColdGrid
{
    public static E5Report Run(
        IReadOnlyDictionary<string, double[,]> fields,
        IReadOnlyList<ReferencePoint> refs,
        CandidateTransform truth,
        (double Min, double Max) scaleBracket,
        int scaleSamples,
        int cropWidth, int cropHeight,
        int gridStepPx,
        int templateSizePx,
        SynthesisProbeWriter writer)
    {
        using var act = SynthesisProbeTracer.Source.StartActivity("experiment.E5");
        var rots = new[] { 0.0, Math.PI };
        var mirrors = new[] { false, true };

        var scales = new double[scaleSamples];
        for (int i = 0; i < scaleSamples; i++)
        {
            double frac = (double)i / (scaleSamples - 1);
            scales[i] = scaleBracket.Min * Math.Pow(scaleBracket.Max / scaleBracket.Min, frac);
        }

        var raw = new List<(CandidateTransform T, double J)>(capacity: 4096);
        int evals = 0;
        foreach (var mirror in mirrors)
            foreach (var rot in rots)
                foreach (var scale in scales)
                    for (int ty = gridStepPx / 2; ty < cropHeight; ty += gridStepPx)
                        for (int tx = gridStepPx / 2; tx < cropWidth; tx += gridStepPx)
                        {
                            var t = new CandidateTransform(scale, rot, mirror, tx, ty);
                            var j = JEvaluator.Evaluate(t, fields, refs).J;
                            raw.Add((t, j));
                            evals++;
                        }

        var top8 = raw.OrderByDescending(p => p.J).Take(8).ToArray();

        var refined = new List<(CandidateTransform T, double J, double Distance)>();
        foreach (var (t, _) in top8)
        {
            var rt = LocalRefine.Run(t, fields, refs, maxIter: 60, stepInit: gridStepPx);
            var rj = JEvaluator.Evaluate(rt, fields, refs).J;
            double dx = rt.Tx - truth.Tx, dy = rt.Ty - truth.Ty;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            refined.Add((rt, rj, dist));
            writer.AppendCsvRow("E5", $"refined_J={rj:0.000}", rt, JEvaluator.Evaluate(rt, fields, refs), dominanceVsRunnerUp: double.NaN);
        }

        double bestDist = refined.Min(x => x.Distance);
        double bestJ = refined.Max(x => x.J);
        act?.SetTag("eval_count", evals);
        act?.SetTag("J_best_after_refine", bestJ);
        act?.SetTag("truth_in_topk", bestDist <= 5.0);
        act?.SetTag("best_distance_px", bestDist);

        return new E5Report(bestDist, bestJ, refined);
    }
}
```

- [ ] **Step 4: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~E5_ColdGridTests"
```

Expected: PASS — `BestDistanceToTruthPx < 5`.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/Experiments/E5_ColdGrid.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/Experiments/E5_ColdGridTests.cs
git commit -m "feat(synthesis-probe): E5 cold grid + top-8 refine + distance-to-truth report"
```

---

### Task 13: ProbeReferences loader (area refs → typed ReferencePoint list)

The probe needs the same 38 refs the production solver uses. The existing tool already has `LandmarksReader` and `NpcsReader` (in `Mithril.MapCalibration.Tools.Common`). We just need to map them into `ReferencePoint` with the right `LandmarkType` strings.

**Files:**
- Create: `tools/MapCalibrationFromScreenshot/SynthesisProbe/ProbeReferences.cs`
- Create: `tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/ProbeReferencesTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// ProbeReferencesTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class ProbeReferencesTests
{
    [Fact]
    public void Loads_eltibule_refs_with_canonical_types()
    {
        // Use the same default paths the existing tool resolves.
        var landmarks = ProbeReferences.DefaultLandmarksPath();
        var npcs = ProbeReferences.DefaultNpcsPath();
        var refs = ProbeReferences.Load(landmarks, npcs, area: "AreaEltibule");

        refs.Should().NotBeEmpty();
        refs.Select(r => r.LandmarkType).Distinct().Should().BeSubsetOf(new[]
        {
            "Portal", "MeditationPillar", "TeleportationPlatform", "Npc",
        });
        // From the handoff: Eltibule has 38 refs.
        refs.Should().HaveCount(38);
    }
}
```

- [ ] **Step 2: Run, verify fails**

Expected: BUILD FAILURE — `ProbeReferences` not found.

- [ ] **Step 3: Implement**

```csharp
// ProbeReferences.cs
using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class ProbeReferences
{
    public static IReadOnlyList<ReferencePoint> Load(string landmarksJson, string npcsJson, string area)
    {
        var result = new List<ReferencePoint>();
        foreach (var l in LandmarksReader.ReadForArea(landmarksJson, area))
        {
            // LandmarksReader yields entries with Type already canonicalized to
            // Portal / MeditationPillar / TeleportationPlatform (the 3 non-Npc types).
            result.Add(new ReferencePoint(l.Name, l.Type, l.X, l.Z));
        }
        foreach (var n in NpcsReader.ReadForArea(npcsJson, area))
        {
            result.Add(new ReferencePoint(n.Name, "Npc", n.X, n.Z));
        }
        return result;
    }

    public static string DefaultLandmarksPath() => RepoPaths.LandmarksJson();
    public static string DefaultNpcsPath() => RepoPaths.NpcsJson();
}
```

> **Note:** if `LandmarksReader.ReadForArea` / `NpcsReader.ReadForArea` have different signatures (return shape, property names), adapt the call sites — the tests-common reader is the source of truth. Same for `RepoPaths`. Verify by reading those files in `tools/Mithril.MapCalibration.Tools.Common/`.

- [ ] **Step 4: Run, verify passes**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release --filter "FullyQualifiedName~ProbeReferencesTests"
```

Expected: PASS — 38 refs.

- [ ] **Step 5: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/ProbeReferences.cs tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests/ProbeReferencesTests.cs
git commit -m "feat(synthesis-probe): load area refs with canonical landmark types"
```

---

### Task 14: SynthesisProbePhase end-to-end wiring

This task replaces the stub from Task 1 with the real entry point.

**Files:**
- Modify: `tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs`

- [ ] **Step 1: Write the wiring**

```csharp
// SynthesisProbePhase.cs (replacing the stub)
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class SynthesisProbePhase
{
    public static int Run(CliArgs args)
    {
        if (args.TruthCal is null)
        {
            Console.Error.WriteLine("--truth-cal required for --phase synthesis-probe");
            return 2;
        }
        if (string.IsNullOrEmpty(args.ScreenshotPath))
        {
            Console.Error.WriteLine("--screenshot required for --phase synthesis-probe");
            return 2;
        }
        if (args.MapRect is null)
        {
            Console.Error.WriteLine("--map-rect required for --phase synthesis-probe (auto-detect is not reliable enough for the diagnostic)");
            return 2;
        }

        using var tracer = SynthesisProbeTracer.Configure(args.TraceConsole, args.OtlpEndpoint);
        using var rootSpan = SynthesisProbeTracer.Source.StartActivity("probe.attempt");
        rootSpan?.SetTag("area", args.Area);
        rootSpan?.SetTag("screenshot", args.ScreenshotPath);

        var truth = new CandidateTransform(
            Scale: args.TruthCal.Value.Scale,
            RotRadians: args.TruthCal.Value.Rot,
            Mirror: args.TruthCal.Value.Mirror,
            Tx: args.TruthCal.Value.Ox,
            Ty: args.TruthCal.Value.Oy);
        rootSpan?.SetTag("truth.scale", truth.Scale);
        rootSpan?.SetTag("truth.rot", truth.RotRadians);
        rootSpan?.SetTag("truth.mirror", truth.Mirror);

        // Load screenshot + aligned base + templates.
        var screenshot = ImageIo.LoadGray(args.ScreenshotPath);
        var (mx, my, mw, mh) = args.MapRect.Value;
        var screenshotCrop = ImageIo.CropGray(screenshot, mx, my, mw, mh);
        rootSpan?.SetTag("crop.w", mw);
        rootSpan?.SetTag("crop.h", mh);

        var pgInstall = SteamInstall.FindPgInstall();
        var mapDir = args.MapDir ?? Path.Combine(Path.GetTempPath(), "mithril-852", "maps");
        var mapPng = MapTextureExtractor.EnsureExtracted(pgInstall, mapDir, args.Area);
        var baseTexture = ImageIo.LoadGray(mapPng);
        var alignedBase = ImageIo.BilinearResize(baseTexture, mw, mh);

        var iconsDir = args.IconsDir ?? Path.Combine(Path.GetTempPath(), "mithril-852", "icons");
        var tpkPath = args.TpkPath ?? Path.Combine(AppContext.BaseDirectory, "classdata.tpk");
        IconTemplateExtractor.EnsureExtracted(pgInstall, iconsDir, tpkPath);
        var templates = IconTemplateExtractor.LoadAll(iconsDir, renderSizePx: args.IconRenderSize > 0 ? args.IconRenderSize : 16);

        var fieldsByType = new Dictionary<string, double[,]>();
        foreach (var template in templates)
        {
            using var fieldSpan = SynthesisProbeTracer.Source.StartActivity("field.build");
            fieldSpan?.SetTag("template.type", template.LandmarkType);
            fieldSpan?.SetTag("template.size_px", template.Width);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            fieldsByType[template.LandmarkType] = IconLikelihoodField.Build(screenshotCrop, alignedBase, template);
            fieldSpan?.SetTag("duration_ms", sw.ElapsedMilliseconds);
        }

        var refs = ProbeReferences.Load(
            args.LandmarksPath ?? ProbeReferences.DefaultLandmarksPath(),
            args.NpcsPath ?? ProbeReferences.DefaultNpcsPath(),
            args.Area);

        var outDir = Path.Combine(RepoPaths.RepoRoot(), "study", "synthesis-probe", args.Area);
        using var writer = new SynthesisProbeWriter(outDir);

        foreach (var (type, field) in fieldsByType) writer.WriteFieldPng(type, field);

        E1_TruthScore.Run(fieldsByType, refs, truth, writer);
        E2_TranslationSweep.Run(fieldsByType, refs, truth, templateSizePx: 16, writer);
        E3_ScaleSweep.Run(fieldsByType, refs, truth, writer);

        if (!string.IsNullOrEmpty(args.RansacSeedsCsvPath))
            E4_RansacSeedScore.Run(fieldsByType, refs, truth, args.RansacSeedsCsvPath, writer);

        E5_ColdGrid.Run(fieldsByType, refs, truth,
            scaleBracket: (0.1, 2.0),
            scaleSamples: 16,
            cropWidth: mw,
            cropHeight: mh,
            gridStepPx: 16,
            templateSizePx: 16,
            writer);

        Console.WriteLine($"[synthesis-probe] artifacts written to {outDir}");
        return 0;
    }
}
```

> **Note:** `ImageIo.CropGray` and `ImageIo.BilinearResize` may need to be added to `Mithril.MapCalibration.Tools.Common/ImageIo.cs` if they don't exist. If they don't, add them as small BCL-only helpers — they're straightforward (slice the byte array; bilinear resample). Treat that as part of Task 14.

- [ ] **Step 2: Build**

```bash
dotnet build tools/MapCalibrationFromScreenshot -c Release
```

Expected: BUILD SUCCESS. Any compile errors → fix; if a method is missing on `ImageIo`/`IconTemplateExtractor`, add a small BCL-only helper in `tools/Mithril.MapCalibration.Tools.Common/`.

- [ ] **Step 3: Run all tests to confirm no regression**

```bash
dotnet test tools/MapCalibrationFromScreenshot.SynthesisProbe.Tests -c Release
```

Expected: PASS — all tests previously green stay green.

- [ ] **Step 4: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/SynthesisProbe/SynthesisProbePhase.cs tools/Mithril.MapCalibration.Tools.Common/ImageIo.cs
git commit -m "feat(synthesis-probe): wire E1-E5 into --phase synthesis-probe end-to-end"
```

---

### Task 15: Frame 2 smoke run (positive control)

Manual diagnostic run, no new code.

**Files:** none new.

- [ ] **Step 1: Locate Frame 2 fixture + its truth calibration**

Fixture: `tests/Mithril.MapCalibration.Harness.Tests/Fixtures/eltibule-frame2-accepted-7.61px.gray.png`

The truth `AreaCalibration` for this frame is what production accepts today (residual 7.61 px). Look it up in `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json` under the `AreaEltibule` key, or run the existing `--phase full` on the same fixture and read off the recovered calibration.

- [ ] **Step 2: Get the map-rect for Frame 2**

The fixture is the cropped post-ECC frame — its map-rect is `0,0,W,H` where W,H are the fixture's pixel dimensions. Read with:

```bash
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- --phase extract-map --area AreaEltibule
```

(populates the base-texture cache so the probe can read it).

- [ ] **Step 3: Run the probe**

```bash
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- `
  --phase synthesis-probe `
  --area AreaEltibule `
  --screenshot tests/Mithril.MapCalibration.Harness.Tests/Fixtures/eltibule-frame2-accepted-7.61px.gray.png `
  --map-rect 0,0,<W>,<H> `
  --truth-cal <scale>,<rot>,<ox>,<oy>,<mirror> `
  --trace-console
```

Replace `<W>`, `<H>` with the fixture pixel dimensions and `<scale>,<rot>,<ox>,<oy>,<mirror>` with Frame 2's truth.

- [ ] **Step 4: Inspect artifacts**

Open `study/synthesis-probe/AreaEltibule/`:

- `field_*.png` — should look like an empty grayscale with bright dots at icon positions.
- `synthesis_probe.csv` — E1's row should show high `J` and high `refs_above_0.5`.
- `grid_landscape_translation.png` — should show a single bright peak at the center.

Spot-check that E5's `truth_in_topk` is `true` in the OTel console output. If Frame 2 fails any of these, the probe itself is broken — do not proceed to Frame 1 until fixed.

- [ ] **Step 5: Commit the Frame 2 artifacts under study/ (optional)**

`study/` is gitignored — these are local-only. No commit step here unless we want to start tracking probe outputs (we don't, per spec out-of-scope).

---

### Task 16: Frame 1 diagnostic run (the decision data)

Manual diagnostic run, no new code.

**Files:** none new.

- [ ] **Step 1: Locate Frame 1 fixture + its truth calibration**

Fixture: `tests/Mithril.MapCalibration.Harness.Tests/Fixtures/eltibule-frame1-rejected-3inliers.gray.png`.

Truth calibration: per the handoff doc, the offline tool's 16-inlier / 1.31 px solve produced it. Run that solve once on Frame 1 to recover the truth, OR look up the value in the harness's `Registration_search_reproduces_ground_truth` test which encodes it.

- [ ] **Step 2: Run the probe**

```bash
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- `
  --phase synthesis-probe `
  --area AreaEltibule `
  --screenshot tests/Mithril.MapCalibration.Harness.Tests/Fixtures/eltibule-frame1-rejected-3inliers.gray.png `
  --map-rect 0,0,<W>,<H> `
  --truth-cal <frame1 truth> `
  --trace-console
```

(If you have a CSV of Frame 1's RANSAC candidates from a previous diagnostic run, also pass `--ransac-seeds-csv <path>` to drive E4.)

- [ ] **Step 3: Analyze**

Open `synthesis_probe.csv` and the landscape PNGs. Apply the decision rules from the spec's §"Decision criteria":

- E1 high + E2/E3 sharp peaks + E5 top-8 contains a ≤5 px-of-truth entry → **Proposal A** (cold synthesis solver).
- E1 high + E2/E3 sharp peaks + E5 misses, but E4 has a near-truth RANSAC seed → **Proposal B** (hybrid).
- Otherwise → neither proposal ships; investigate why the field isn't informative.

- [ ] **Step 4: Record the decision in the spec doc**

Edit `docs/superpowers/specs/2026-06-01-synthesis-probe-diagnostic-design.md`, append a "Result" section under `## Decision criteria` with the actual numbers measured + the A-vs-B-vs-neither verdict. Commit:

```bash
git add docs/superpowers/specs/2026-06-01-synthesis-probe-diagnostic-design.md
git commit -m "docs(synthesis-probe): record Frame 1 diagnostic result + architecture verdict"
```

---

### Task 17: README update

**Files:**
- Modify: `tools/MapCalibrationFromScreenshot/README.md`

- [ ] **Step 1: Add a section for the new phase**

Append (just before the "Out of scope (v1)" section):

````markdown
## `--phase synthesis-probe` — icon-likelihood-field diagnostic

Standalone experiment runner that scores the synthesis objective `J(T) = Σ L_{type(r)}(T·r)` across five experiments on a given screenshot. Output is a CSV + per-type field PNGs + a translation landscape PNG, plus OTel spans. The data decides which of two production-solver proposals to build (cold synthesis vs. RANSAC-seeded hybrid). See [`docs/superpowers/specs/2026-06-01-synthesis-probe-diagnostic-design.md`](../../docs/superpowers/specs/2026-06-01-synthesis-probe-diagnostic-design.md).

```powershell
dotnet run --project tools/MapCalibrationFromScreenshot -c Release -- `
  --phase synthesis-probe `
  --area AreaEltibule `
  --screenshot path/to/screenshot.png `
  --map-rect x,y,w,h `
  --truth-cal scale,rot,ox,oy,mirror `
  --trace-console
```

Artifacts are written to `study/synthesis-probe/<area>/`.

Flags specific to this phase: `--truth-cal`, `--ransac-seeds-csv`, `--trace-console`, `--otlp`. The full flag table near the top of this README documents each.
````

- [ ] **Step 2: Commit**

```bash
git add tools/MapCalibrationFromScreenshot/README.md
git commit -m "docs(synthesis-probe): document --phase synthesis-probe in tool README"
```

---

## Self-Review Notes

(Run after writing the plan; record findings inline above.)

**Spec coverage:** Each spec section maps to at least one task —
- §"The math" → Tasks 4, 5
- §"Tool surface" → Task 1, 3, 17
- §"Outputs" → Tasks 7, 14
- §"Experiments E1–E5" → Tasks 9, 10, 12, 14
- §"OTel instrumentation" → Task 8, 14
- §"Decision criteria" → Task 16
- §"Out of scope" — implicit; no task creates production code.
- §"Open questions / Verification owed" — partially deferred to Task 16's analysis step (truth re-derivation noted as open in the spec; not a blocker for v0 since we proceed with the tool's existing 16-inlier solve).

**Type consistency:** `CandidateTransform.Apply` is used in `JEvaluator` (Task 6), `LocalRefine` (Task 11), `E5_ColdGrid` (Task 12), and `SynthesisProbePhase` (Task 14). `ReferencePoint.LandmarkType` is a `string` matching the four canonical types declared in `CanonicalLandmarkTypes` ([src/Mithril.MapCalibration/Detection/CanonicalLandmarkTypes.cs](../../../../../../../src/Mithril.MapCalibration/Detection/CanonicalLandmarkTypes.cs)); confirm at Task 13 implementation time that `LandmarksReader.ReadForArea`'s `Type` property is already in that vocabulary (per CLAUDE.md memory `map_calibration_engine_914_plan`/handoff §"#974", it is).

**Placeholders:** none.

---

## Execution Handoff (to be performed after plan acceptance)

Plan complete and saved to `docs/superpowers/plans/2026-06-01-synthesis-probe-diagnostic.md`. Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — execute tasks in this session using `superpowers:executing-plans`, batch execution with checkpoints.

Choose at execution time.
