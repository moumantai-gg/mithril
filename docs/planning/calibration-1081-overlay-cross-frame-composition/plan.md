# Legolas overlay cross-frame composition — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Legolas overlay render markers correctly for any scene whose only calibration is AutoCalibration-produced (texture-frame). Cal stamps `PixelSha256`; the existing canonical-asset-hash catalogue extends from sha-only to `{ sha, width, height }` so the overlay can resolve texture dimensions by content-address; the renderer composes per frame via [`WorldToTextureCalibration.ProjectThroughOverlay(MapRect)`](../../../src/Mithril.MapCalibration/WorldToTextureCalibration.cs).

**Architecture:** AutoCal stamps SHA-256 of the base texture's gray pixels onto the persisted `AreaCalibration`. The same `canonical-asset-hashes.json` resource the hash gate ships gets bumped Schema v1→v2 to carry per-entry dims alongside the sha. New `IMapTextureDimensions` service in core does a sha→(W,H) lookup; the overlay's `OnSurfaceRender` resolves a composed `WorldToOverlayCalibration?` per frame and threads it through `BeginFrame` to both marker projection and scene-drawer `Project`. Catalogue types lift from `Mithril.MapCalibration.Detection` to core so `Mithril.Overlay` doesn't grow a Detection dependency.

**Tech Stack:** C# .NET 10 / WPF, `Mithril.MapCalibration`, `Mithril.MapCalibration.Detection`, `Mithril.MapCalibration.Capture`, `Mithril.Overlay`, xunit + FluentAssertions, `System.Diagnostics.ActivitySource`, `System.Security.Cryptography.SHA256`.

---

## File structure (locked decomposition)

**Move (core promotion):**
- Delete: `src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashes.cs`
- Create: `src/Mithril.MapCalibration/CanonicalAssetHashEntry.cs` (new public record)
- Create: `src/Mithril.MapCalibration/CanonicalAssetHashes.cs` (lifted public record, v2 shape)

**Modify (core):**
- [`src/Mithril.MapCalibration/AreaCalibration.cs`](../../../src/Mithril.MapCalibration/AreaCalibration.cs) — add `PixelSha256` init-only field
- [`src/Mithril.MapCalibration/WorldToTextureCalibration.cs`](../../../src/Mithril.MapCalibration/WorldToTextureCalibration.cs) — mirror `PixelSha256`
- [`src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`](../../../src/Mithril.MapCalibration/Internal/MapCalibrationService.cs) — `ToTextureCalibration` threads `PixelSha256` through
- [`src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs`](../../../src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs) — register `CanonicalAssetHashes` + `CanonicalAssetHashEntry`
- Create: `src/Mithril.MapCalibration/IMapTextureDimensions.cs`
- Create: `src/Mithril.MapCalibration/Internal/CatalogueMapTextureDimensions.cs`
- Create: `src/Mithril.MapCalibration/Internal/CanonicalAssetHashesLoader.cs` (v1→v2 wrapping)
- [`src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`](../../../src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs) — register `IMapTextureDimensions`

**Modify (Detection):**
- [`src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashGate.cs`](../../../src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashGate.cs) — switch to core's `CanonicalAssetHashes`; route loading through `CanonicalAssetHashesLoader`
- [`src/Mithril.MapCalibration.Detection/Internal/DetectionJsonContext.cs`](../../../src/Mithril.MapCalibration.Detection/Internal/DetectionJsonContext.cs) — registration now resolves the core type (no source change unless explicit `using`)

**Modify (Capture):**
- [`src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs) (~line 717) — stamp `PixelSha256 = Convert.ToHexStringLower(SHA256.HashData(baseTexture.Pixels))`

**Modify (Bundled data):**
- [`src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json`](../../../src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json) — populate v2 shape with current PG version's entries
- [`src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`](../../../src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json) — stamp `pixelSha256` per row

**Modify (Overlay):**
- [`src/Mithril.Overlay/Internal/OverlayWindowService.cs`](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs) — `ResolveComposedOverlayCalibration` helper; `BeginFrame` grows `composedCal`; `OverlaySceneContext.Project` reads bound cal; `ProjectMarkers` reshape; `cal.path` span tag
- [`src/Mithril.Overlay/Internal/OverlayWindow.xaml.cs`](../../../src/Mithril.Overlay/Internal/OverlayWindow.xaml.cs) — expose `OverlaySurface` accessor if not already public

**Create (tests):**
- `tests/Mithril.MapCalibration.Tests/AreaCalibrationTextureShaTests.cs` (sha round-trip, absent-field → null)
- `tests/Mithril.MapCalibration.Tests/CanonicalAssetHashesV1V2LoadTests.cs` (Schema v1→v2 wrapping)
- `tests/Mithril.MapCalibration.Tests/MapTextureDimensionsTests.cs` (sha index)
- `tests/Mithril.MapCalibration.Tests/BundledCatalogueLintTests.cs` (collision + baseline coverage)
- `tests/Mithril.MapCalibration.Tests/WorldToTextureCalibrationTests.cs` *(extend — sha carries through struct)*
- `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineShaStampTests.cs` (or sibling — engine stamps sha on persisted record)
- `tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs` (decision-table)

**Modify (tests):**
- [`tests/Mithril.MapCalibration.Tests/Detection/CanonicalAssetHashGateTests.cs`](../../../tests/Mithril.MapCalibration.Tests/Detection/CanonicalAssetHashGateTests.cs) — adapt to lifted public types
- [`tests/Mithril.Overlay.Tests/OverlaySceneHookTests.cs`](../../../tests/Mithril.Overlay.Tests/OverlaySceneHookTests.cs) — adapt `Project_plumbs_current_zoom` to the bound-cal seam; add texture-frame integration fact
- [`tests/Mithril.Overlay.Tests/Fakes/FakeMapCalibrationService.cs`](../../../tests/Mithril.Overlay.Tests/Fakes/FakeMapCalibrationService.cs) — hookable `GetOverlayCalibration` / `GetTextureCalibration`

**Modify (docs):**
- [`docs/perf-trace-schema.md`](../../../docs/perf-trace-schema.md) — document `cal.path` tag

---

## Task 1: Lift `CanonicalAssetHashes` + `CanonicalAssetHashEntry` to core (v2 shape)

**Files:**
- Delete: `src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashes.cs`
- Create: `src/Mithril.MapCalibration/CanonicalAssetHashEntry.cs`
- Create: `src/Mithril.MapCalibration/CanonicalAssetHashes.cs`

- [ ] **Step 1: Create the new public entry record in core**

Create `src/Mithril.MapCalibration/CanonicalAssetHashEntry.cs`:

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// One catalogue entry: the canonical SHA-256 (lowercase hex) and native pixel
/// dimensions of a base texture, harvested from the asset-extractor sidecar's
/// <c>map-texture-&lt;X&gt;.json</c> manifest at Mithril release time. The hash
/// gate (<c>Mithril.MapCalibration.Detection.Internal.CanonicalAssetHashGate</c>)
/// reads <see cref="Sha"/>; the overlay's dim resolver
/// (<see cref="IMapTextureDimensions"/>) reads <see cref="Width"/> +
/// <see cref="Height"/>. One catalogue, two consumers. mithril#1081 Schema v2.
/// </summary>
public sealed record CanonicalAssetHashEntry(
    string Sha,
    int    Width,
    int    Height);
```

- [ ] **Step 2: Create the new public catalogue record in core**

Create `src/Mithril.MapCalibration/CanonicalAssetHashes.cs`:

```csharp
using System.Collections.Generic;

namespace Mithril.MapCalibration;

/// <summary>
/// The committed catalogue of canonical (validated-once) per-asset truth keyed
/// by Project Gorgon version: <c>byPgVersion["&lt;pg&gt;"]["&lt;artifactKey&gt;"]
/// = { sha, width, height }</c>. Artifact keys mirror the existing hash gate's
/// format — for map textures, the literal Unity Texture2D name with the
/// <c>Map_</c> prefix (e.g. <c>Map_AreaSerbule</c>); for icons, the sentinel
/// <c>"icons"</c>.
///
/// <para>Schema v1 (pre-#1081) carried bare-string values (sha only); v2 widens
/// to the <see cref="CanonicalAssetHashEntry"/> record. v1 files load via the
/// loader's wrapping fallback (zero dims) — hash-gate consumers continue to read
/// <see cref="CanonicalAssetHashEntry.Sha"/>; dim consumers see 0/0 → catalogue
/// miss → fail-soft. mithril#1081 lifts this type from <c>.Detection.Internal</c>
/// to core so <c>Mithril.Overlay</c> can consume the dim slice without crossing
/// the Detection assembly boundary.</para>
/// </summary>
public sealed record CanonicalAssetHashes(
    int SchemaVersion,
    Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>> ByPgVersion);
```

- [ ] **Step 3: Delete the now-superseded internal record**

```pwsh
Remove-Item "src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashes.cs"
```

- [ ] **Step 4: Confirm Detection still compiles by updating `using` clauses**

The hash gate uses `CanonicalAssetHashes` and `CanonicalAssetHashGate` references — both presently in `Mithril.MapCalibration.Detection.Internal`. After the lift, code that references `CanonicalAssetHashes` resolves it from core (`Mithril.MapCalibration`); both assemblies' `using` statements may need a `using Mithril.MapCalibration;` added.

Build the Detection project:

```pwsh
dotnet build src/Mithril.MapCalibration.Detection -v minimal
```

Expected: most likely compile errors at `CanonicalAssetHashGate.cs` and `DetectionJsonContext.cs` because the type name now resolves only with `using Mithril.MapCalibration;`. Fix each by adding the using.

Also: the gate's existing `string`-valued lookup (`byArtifact.TryGetValue(artifactKey, out var expected)`) now returns `CanonicalAssetHashEntry`, not `string`. The gate's `expected` variable is read as a string — must update to read `expected.Sha`. Tasks 2 + 3 handle this.

- [ ] **Step 5: Commit (red — build is intentionally broken until Task 2 lands)**

```bash
git add src/Mithril.MapCalibration/CanonicalAssetHashEntry.cs src/Mithril.MapCalibration/CanonicalAssetHashes.cs src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashes.cs
git commit -m "$(cat <<'EOF'
refactor(map-calibration): lift CanonicalAssetHashes + new Entry type to core (#1081)

Move CanonicalAssetHashes from Detection.Internal to core's public surface;
widen the inner-dict value from bare string (sha-only) to a new public
CanonicalAssetHashEntry record { Sha, Width, Height } so the catalogue
serves as the dim oracle for the overlay's cross-frame composition.

Build is intentionally red after this commit — Task 2 updates the gate +
JSON context to consume the lifted types.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Update `CanonicalAssetHashGate` + `DetectionJsonContext` to consume lifted types

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashGate.cs`
- Modify: `src/Mithril.MapCalibration.Detection/Internal/DetectionJsonContext.cs`

- [ ] **Step 1: Add `using Mithril.MapCalibration;` to both files**

Open both files. Add `using Mithril.MapCalibration;` near the top, sorted with the existing usings.

- [ ] **Step 2: Update `CanonicalAssetHashGate.Check` to read `.Sha` instead of bare string**

Replace lines ~74-99 of `CanonicalAssetHashGate.cs`:

```csharp
public HashVerdict Check(string? pgVersion, string artifactKey, string actualSha256)
{
    if (string.IsNullOrWhiteSpace(pgVersion))
    {
        _logger?.LogWarning(
            "Canonical-hash gate: no PG version supplied for {Artifact} — accept-with-warn (cannot look up catalogue).",
            artifactKey);
        return new HashVerdict(true, true, "no PG version supplied");
    }

    if (!_catalogue.ByPgVersion.TryGetValue(pgVersion, out var byArtifact))
    {
        _logger?.LogWarning(
            "Canonical-hash gate: PG version {PgVersion} not in catalogue for {Artifact} — accept-with-warn (newer patch?).",
            pgVersion, artifactKey);
        return new HashVerdict(true, true, $"PG version {pgVersion} not catalogued");
    }

    if (!byArtifact.TryGetValue(artifactKey, out var entry) || string.IsNullOrEmpty(entry?.Sha))
    {
        _logger?.LogWarning(
            "Canonical-hash gate: no canonical hash for {Artifact} under PG {PgVersion} — accept-with-warn.",
            artifactKey, pgVersion);
        return new HashVerdict(true, true, $"no canonical hash for {artifactKey} under {pgVersion}");
    }

    if (string.Equals(entry.Sha, actualSha256, StringComparison.OrdinalIgnoreCase))
    {
        return new HashVerdict(true, false, "match");
    }

    _logger?.LogWarning(
        "Canonical-hash gate: hash mismatch for {Artifact} under PG {PgVersion} (canonical {Expected}, actual {Actual}) — rejected (decode-tool drift / corruption).",
        artifactKey, pgVersion, entry.Sha, actualSha256);
    return new HashVerdict(false, false, $"hash mismatch (canonical {entry.Sha}, actual {actualSha256})");
}
```

Update the `Load()` fallback construction (line ~52) to use an empty-dict-of-entries:

```csharp
public static CanonicalAssetHashGate Load(ILogger? logger)
{
    var catalogue = ReadCatalogue(logger)
        ?? new CanonicalAssetHashes(2, new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>(StringComparer.Ordinal));
    return new CanonicalAssetHashGate(catalogue, logger);
}
```

(SchemaVersion bumped to 2 in the fallback so the empty-catalogue path matches the new shape.)

- [ ] **Step 3: `DetectionJsonContext.cs` registration**

The existing `[JsonSerializable(typeof(CanonicalAssetHashes))]` still resolves (now to the lifted core type). Add `[JsonSerializable(typeof(CanonicalAssetHashEntry))]` so the source generator emits the nested record's serializer:

```csharp
[JsonSerializable(typeof(OrbDescriptorManifest))]
[JsonSerializable(typeof(IconTemplateManifest))]
[JsonSerializable(typeof(MapTextureManifest))]
[JsonSerializable(typeof(CanonicalAssetHashes))]
[JsonSerializable(typeof(CanonicalAssetHashEntry))]   // new
[JsonSerializable(typeof(SidecarResult))]
internal partial class DetectionJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Build Detection**

```pwsh
dotnet build src/Mithril.MapCalibration.Detection -v minimal
```

Expected: success.

- [ ] **Step 5: Build solution**

```pwsh
dotnet build Mithril.slnx -v minimal
```

Expected: success.

- [ ] **Step 6: Run existing gate tests**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~CanonicalAssetHashGateTests" -v minimal
```

Existing tests in `tests/Mithril.MapCalibration.Tests/Detection/CanonicalAssetHashGateTests.cs` likely construct a `CanonicalAssetHashes` with bare-string inner dict and may fail to compile. Adapt the test fixtures to use `CanonicalAssetHashEntry`:

```csharp
// before
var catalogue = new CanonicalAssetHashes(1, new Dictionary<string, Dictionary<string, string>>
{
    ["1.234"] = new() { ["AreaTest"] = "abc123" }
});

// after
var catalogue = new CanonicalAssetHashes(2, new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>
{
    ["1.234"] = new() { ["AreaTest"] = new CanonicalAssetHashEntry("abc123", 1024, 1024) }
});
```

Apply the same shape change to every test fixture in that file. Re-run:

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~CanonicalAssetHashGateTests" -v minimal
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Mithril.MapCalibration.Detection tests/Mithril.MapCalibration.Tests/Detection
git commit -m "$(cat <<'EOF'
refactor(map-calibration): adapt hash gate + tests to lifted Entry type (#1081)

Gate reads .Sha off CanonicalAssetHashEntry instead of the bare string;
existing tests rewritten to construct entries. Behaviour unchanged at the
gate surface (HashVerdict shape preserved). Solution builds clean.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Schema v1→v2 backwards-compat loader

**Files:**
- Create: `src/Mithril.MapCalibration/Internal/CanonicalAssetHashesLoader.cs`
- Modify: `src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashGate.cs` (route load through the new loader)
- Create: `tests/Mithril.MapCalibration.Tests/CanonicalAssetHashesV1V2LoadTests.cs`

- [ ] **Step 1: Write the failing test for v1→v2 wrapping**

Create `tests/Mithril.MapCalibration.Tests/CanonicalAssetHashesV1V2LoadTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1081 — the canonical-asset-hashes catalogue widens from
/// Schema v1 (`byPgVersion[pg][key] = "sha"`) to v2
/// (`byPgVersion[pg][key] = { sha, width, height }`). v1 files load via the
/// loader's wrapping fallback so existing hash-gate consumers don't break;
/// dim consumers see 0/0 and treat as catalogue miss.
/// </summary>
public sealed class CanonicalAssetHashesV1V2LoadTests
{
    [Fact]
    public void V1Json_LoadsWithWrappedEntries_ZeroDims()
    {
        var v1Json = """
            {
              "schemaVersion": 1,
              "byPgVersion": {
                "1.234": {
                  "Map_AreaTest": "abc123def"
                }
              }
            }
            """;

        var catalogue = CanonicalAssetHashesLoader.Parse(v1Json);

        catalogue.SchemaVersion.Should().Be(1, "loader preserves the source schema version for diagnostics");
        catalogue.ByPgVersion.Should().ContainKey("1.234");
        var entry = catalogue.ByPgVersion["1.234"]["Map_AreaTest"];
        entry.Sha.Should().Be("abc123def");
        entry.Width.Should().Be(0, "v1 records had no dims → dim consumers see catalogue miss");
        entry.Height.Should().Be(0);
    }

    [Fact]
    public void V2Json_LoadsNatively()
    {
        var v2Json = """
            {
              "schemaVersion": 2,
              "byPgVersion": {
                "1.234": {
                  "Map_AreaTest": { "sha": "abc123def", "width": 1024, "height": 768 }
                }
              }
            }
            """;

        var catalogue = CanonicalAssetHashesLoader.Parse(v2Json);

        catalogue.SchemaVersion.Should().Be(2);
        var entry = catalogue.ByPgVersion["1.234"]["Map_AreaTest"];
        entry.Sha.Should().Be("abc123def");
        entry.Width.Should().Be(1024);
        entry.Height.Should().Be(768);
    }

    [Fact]
    public void EmptyV1Stub_LoadsWithEmptyByPgVersion()
    {
        // The catalogue file ships today as an empty v1 stub.
        var stubJson = """{ "schemaVersion": 1, "byPgVersion": {} }""";

        var catalogue = CanonicalAssetHashesLoader.Parse(stubJson);

        catalogue.ByPgVersion.Should().BeEmpty();
    }

    [Fact]
    public void MissingSchemaVersion_DefaultsToV1Wrapping()
    {
        // Defensive: if a future hand-edit accidentally omits schemaVersion,
        // the loader treats the file as v1 (bare-string values) to avoid a
        // hard-crash on a recoverable shape.
        var noVersionJson = """
            {
              "byPgVersion": {
                "1.234": { "Map_AreaTest": "abc123def" }
              }
            }
            """;

        var catalogue = CanonicalAssetHashesLoader.Parse(noVersionJson);

        catalogue.ByPgVersion["1.234"]["Map_AreaTest"].Sha.Should().Be("abc123def");
        catalogue.ByPgVersion["1.234"]["Map_AreaTest"].Width.Should().Be(0);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~CanonicalAssetHashesV1V2LoadTests" -v minimal
```

Expected: FAIL — `CanonicalAssetHashesLoader` doesn't exist.

- [ ] **Step 3: Implement the loader**

Create `src/Mithril.MapCalibration/Internal/CanonicalAssetHashesLoader.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Schema v1 → v2 tolerant loader for canonical-asset-hashes.json
/// (mithril#1081). The on-disk shape's inner-dict value changed from
/// <c>string</c> (sha-only) to <see cref="CanonicalAssetHashEntry"/>
/// (sha + width + height). v1 values are wrapped on load with
/// <c>Width = Height = 0</c>; dim consumers see a catalogue miss for
/// v1-loaded entries and fail-soft to "no render", while hash-gate
/// consumers continue to read <see cref="CanonicalAssetHashEntry.Sha"/>
/// without changing behaviour.
/// </summary>
internal static class CanonicalAssetHashesLoader
{
    /// <summary>Load the catalogue from <paramref name="stream"/>. Returns
    /// null on read / parse failure so the caller can fail-soft to an
    /// empty catalogue (same posture as today).</summary>
    public static CanonicalAssetHashes? TryLoad(Stream stream, ILogger? logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(stream);
            return Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Canonical-asset-hashes JSON failed to parse — gate accepts all (safe-degrade).");
            return null;
        }
        catch (IOException ex)
        {
            logger?.LogWarning(ex, "Canonical-asset-hashes stream failed to read — gate accepts all (safe-degrade).");
            return null;
        }
    }

    /// <summary>Test-friendly overload. Parses a JSON string; throws on
    /// malformed JSON (tests rely on the throw to catch fixture bugs).</summary>
    public static CanonicalAssetHashes Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    private static CanonicalAssetHashes Parse(JsonElement root)
    {
        var schemaVersion = root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number
            ? sv.GetInt32()
            : 1;
        var isV1 = schemaVersion < 2;

        var byPg = new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>(System.StringComparer.Ordinal);
        if (root.TryGetProperty("byPgVersion", out var bpv) && bpv.ValueKind == JsonValueKind.Object)
        {
            foreach (var pgVersionProp in bpv.EnumerateObject())
            {
                var byArtifact = new Dictionary<string, CanonicalAssetHashEntry>(System.StringComparer.Ordinal);
                foreach (var artifactProp in pgVersionProp.Value.EnumerateObject())
                {
                    byArtifact[artifactProp.Name] = ReadEntry(artifactProp.Value, isV1);
                }
                byPg[pgVersionProp.Name] = byArtifact;
            }
        }
        return new CanonicalAssetHashes(schemaVersion, byPg);
    }

    private static CanonicalAssetHashEntry ReadEntry(JsonElement element, bool isV1)
    {
        if (isV1 || element.ValueKind == JsonValueKind.String)
        {
            // v1: bare-string sha. Wrap with zero dims (dim consumers see catalogue miss).
            return new CanonicalAssetHashEntry(element.GetString() ?? string.Empty, 0, 0);
        }

        // v2: { sha, width, height } object.
        var sha = element.TryGetProperty("sha", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
        var width = element.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
        var height = element.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : 0;
        return new CanonicalAssetHashEntry(sha, width, height);
    }
}
```

- [ ] **Step 4: Route the gate's load path through the loader**

Replace `CanonicalAssetHashGate.ReadCatalogue` (lines ~101-119) to delegate:

```csharp
private static CanonicalAssetHashes? ReadCatalogue(ILogger? logger)
{
    var assembly = typeof(CanonicalAssetHashGate).Assembly;
    using var stream = assembly.GetManifestResourceStream(CatalogueResource);
    if (stream is null)
    {
        logger?.LogWarning("Canonical-asset-hash catalogue {Resource} not found — gate accepts all (safe-degrade).", CatalogueResource);
        return null;
    }
    return CanonicalAssetHashesLoader.TryLoad(stream, logger);
}
```

Add `using Mithril.MapCalibration.Internal;` at the top of the file.

The `CatalogueResource` constant remains `"Mithril.MapCalibration.BundledData.canonical-asset-hashes.json"` — that resource ships in the core assembly, not Detection. Verify by checking `src/Mithril.MapCalibration/Mithril.MapCalibration.csproj` includes it as an embedded resource (it does — the file is the data Detection's gate reads).

- [ ] **Step 5: Run the loader tests**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~CanonicalAssetHashesV1V2LoadTests" -v minimal
```

Expected: PASS (4/4).

- [ ] **Step 6: Run the full hash-gate test suite to confirm no regression**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~CanonicalAssetHashGate" -v minimal
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Mithril.MapCalibration/Internal/CanonicalAssetHashesLoader.cs src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashGate.cs tests/Mithril.MapCalibration.Tests/CanonicalAssetHashesV1V2LoadTests.cs
git commit -m "$(cat <<'EOF'
feat(map-calibration): Schema v1→v2 tolerant catalogue loader (#1081)

CanonicalAssetHashesLoader handles both the v1 (bare-string sha) and v2
({sha, width, height}) on-disk shapes. v1 entries wrap with zero dims;
hash-gate consumers see no change, dim consumers (next task) see
catalogue miss and fail-soft.

CanonicalAssetHashGate.ReadCatalogue now delegates to the loader so the
gate stays a thin wrapper over the shared catalogue read path.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `IMapTextureDimensions` service in core

**Files:**
- Create: `src/Mithril.MapCalibration/IMapTextureDimensions.cs`
- Create: `src/Mithril.MapCalibration/Internal/CatalogueMapTextureDimensions.cs`
- Modify: `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`
- Create: `tests/Mithril.MapCalibration.Tests/MapTextureDimensionsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Mithril.MapCalibration.Tests/MapTextureDimensionsTests.cs`:

```csharp
using System.Collections.Generic;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1081 — IMapTextureDimensions is the overlay's content-addressed
/// resolver for "given a base texture's sha, what are its native pixel
/// dimensions?" Backed by the canonical-asset-hash catalogue; the
/// implementation pre-builds a sha→(W,H) index across all PG versions so
/// the lookup is O(1) at render time.
/// </summary>
public sealed class MapTextureDimensionsTests
{
    private static CanonicalAssetHashes Catalogue(params (string Pg, string Key, string Sha, int W, int H)[] entries)
    {
        var byPg = new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>(System.StringComparer.Ordinal);
        foreach (var (pg, key, sha, w, h) in entries)
        {
            if (!byPg.TryGetValue(pg, out var inner))
                inner = byPg[pg] = new Dictionary<string, CanonicalAssetHashEntry>(System.StringComparer.Ordinal);
            inner[key] = new CanonicalAssetHashEntry(sha, w, h);
        }
        return new CanonicalAssetHashes(2, byPg);
    }

    [Fact]
    public void KnownSha_ReturnsDims()
    {
        var dims = new CatalogueMapTextureDimensions(Catalogue(
            ("467", "Map_AreaSerbule", "abc", 1024, 1024),
            ("467", "Map_AreaEltibule", "def", 2048, 1024)));

        dims.TryGetSizeBySha("abc").Should().Be((1024, 1024));
        dims.TryGetSizeBySha("def").Should().Be((2048, 1024));
    }

    [Fact]
    public void UnknownSha_ReturnsNull()
    {
        var dims = new CatalogueMapTextureDimensions(Catalogue(
            ("467", "Map_AreaSerbule", "abc", 1024, 1024)));

        dims.TryGetSizeBySha("notinhere").Should().BeNull();
    }

    [Fact]
    public void NullOrEmptySha_ReturnsNull()
    {
        var dims = new CatalogueMapTextureDimensions(Catalogue(
            ("467", "Map_AreaSerbule", "abc", 1024, 1024)));

        dims.TryGetSizeBySha(null).Should().BeNull();
        dims.TryGetSizeBySha("").Should().BeNull();
        dims.TryGetSizeBySha("   ").Should().BeNull();
    }

    [Fact]
    public void ZeroDimEntries_SkippedFromIndex()
    {
        // v1-wrapped entries (loaded with 0/0 dims) must not poison the
        // sha→(W,H) index — they're catalogue misses by construction.
        var dims = new CatalogueMapTextureDimensions(Catalogue(
            ("467", "Map_AreaSerbule", "abc", 0, 0)));

        dims.TryGetSizeBySha("abc").Should().BeNull();
    }

    [Fact]
    public void SameShaAcrossPgVersions_LastWriterWins()
    {
        // Same sha (= same pixel content) under two PG versions must have
        // matching dims (the lint test guarantees this); if a future build
        // somehow ships conflicting dims, the index resolves to whichever
        // entry was enumerated last. Behaviour is observable but not
        // semantically meaningful — both values are equivalently "right"
        // for the sha, and the lint test fails the build before this matters.
        var dims = new CatalogueMapTextureDimensions(Catalogue(
            ("467", "Map_AreaSerbule", "abc", 1024, 1024),
            ("468", "Map_AreaSerbule", "abc", 1024, 1024)));

        dims.TryGetSizeBySha("abc").Should().Be((1024, 1024));
    }

    [Fact]
    public void EmptyCatalogue_AllLookupsNull()
    {
        var dims = new CatalogueMapTextureDimensions(new CanonicalAssetHashes(
            2, new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>()));

        dims.TryGetSizeBySha("abc").Should().BeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapTextureDimensionsTests" -v minimal
```

Expected: FAIL — neither type exists yet.

- [ ] **Step 3: Create the interface**

Create `src/Mithril.MapCalibration/IMapTextureDimensions.cs`:

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// Content-addressed resolver for base-texture pixel dimensions, backed by the
/// canonical-asset-hash catalogue. The overlay's per-frame composer
/// (mithril#1081 / <see cref="WorldToTextureCalibration.ProjectThroughOverlay"/>)
/// queries this with the calibration record's stamped
/// <see cref="AreaCalibration.PixelSha256"/> to build the
/// <see cref="MapRect"/> describing where the base texture renders on the
/// overlay surface.
///
/// <para>Catalogue maintenance: ships in
/// <c>BundledData/canonical-asset-hashes.json</c>, refreshed per Mithril
/// release alongside the existing canonical-hash gate. PG-version-agnostic
/// at the lookup layer (same sha = same pixel content = same dims by
/// definition).</para>
/// </summary>
public interface IMapTextureDimensions
{
    /// <summary>Look up the canonical (width, height) for a texture by its
    /// SHA-256 (lowercase hex). Returns null when:
    /// <list type="bullet">
    /// <item><paramref name="pixelSha256"/> is null/empty (e.g. a pre-#1081
    /// calibration record);</item>
    /// <item>the catalogue has no entry for the sha (newer PG patch than
    /// Mithril release, or an uncatalogued asset);</item>
    /// <item>the entry exists but carries zero dims (a v1-wrapped catalogue
    /// entry — same fail-soft as a real miss).</item>
    /// </list>
    /// Fail-soft by design — the overlay treats null as "skip this scene
    /// this frame," matching the existing hash-gate's accept-with-warn
    /// posture for uncatalogued assets.</summary>
    (int Width, int Height)? TryGetSizeBySha(string? pixelSha256);
}
```

- [ ] **Step 4: Create the implementation**

Create `src/Mithril.MapCalibration/Internal/CatalogueMapTextureDimensions.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Default <see cref="IMapTextureDimensions"/> impl. Pre-builds a sha→(W,H)
/// index across all PG versions in the catalogue so the lookup is O(1) at
/// render path. Zero-dim entries (v1-wrapped catalogue records) are
/// excluded — they signal "uncatalogued; fail-soft."
/// </summary>
internal sealed class CatalogueMapTextureDimensions : IMapTextureDimensions
{
    private readonly IReadOnlyDictionary<string, (int W, int H)> _bySha;

    public CatalogueMapTextureDimensions(CanonicalAssetHashes catalogue)
    {
        var idx = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var byArtifact in catalogue.ByPgVersion.Values)
        {
            foreach (var entry in byArtifact.Values)
            {
                if (entry.Width > 0 && entry.Height > 0)
                {
                    idx[entry.Sha] = (entry.Width, entry.Height);
                }
            }
        }
        _bySha = idx;
    }

    public (int Width, int Height)? TryGetSizeBySha(string? pixelSha256)
    {
        if (string.IsNullOrWhiteSpace(pixelSha256)) return null;
        return _bySha.TryGetValue(pixelSha256!, out var dims) ? (dims.W, dims.H) : null;
    }
}
```

- [ ] **Step 5: Register in DI**

Open `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`. Add the registration alongside the existing service registrations:

```csharp
// mithril#1081 — content-addressed texture-dim resolver. Loads the same
// canonical-asset-hashes.json resource the hash gate reads, indexed by sha
// for O(1) lookup at the overlay render path.
services.AddSingleton<IMapTextureDimensions>(sp =>
{
    var loggerFactory = sp.GetService<ILoggerFactory>();
    var logger = loggerFactory?.CreateLogger("Mithril.MapCalibration.MapTextureDimensions");
    var assembly = typeof(CatalogueMapTextureDimensions).Assembly;
    using var stream = assembly.GetManifestResourceStream("Mithril.MapCalibration.BundledData.canonical-asset-hashes.json");
    var catalogue = stream is not null
        ? CanonicalAssetHashesLoader.TryLoad(stream, logger)
        : null;
    catalogue ??= new CanonicalAssetHashes(2, new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>(StringComparer.Ordinal));
    return new CatalogueMapTextureDimensions(catalogue);
});
```

Add `using Mithril.MapCalibration.Internal;` and `using Microsoft.Extensions.Logging;` if not already present.

- [ ] **Step 6: Run the tests**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapTextureDimensionsTests" -v minimal
```

Expected: PASS (6/6).

- [ ] **Step 7: Commit**

```bash
git add src/Mithril.MapCalibration/IMapTextureDimensions.cs src/Mithril.MapCalibration/Internal/CatalogueMapTextureDimensions.cs src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs tests/Mithril.MapCalibration.Tests/MapTextureDimensionsTests.cs
git commit -m "$(cat <<'EOF'
feat(map-calibration): IMapTextureDimensions content-addressed resolver (#1081)

New tiny service in core that pre-builds a sha→(W,H) index over the
canonical-asset-hash catalogue. The overlay's per-frame composer queries
this with the calibration record's stamped PixelSha256 to derive the
MapRect for WorldToTextureCalibration.ProjectThroughOverlay.

Fail-soft by design: null/empty sha → null; catalogue miss → null;
zero-dim entries (v1-wrapped) → null. All map onto the renderer's
"skip this scene this frame" branch.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Add `PixelSha256` to `AreaCalibration` + `WorldToTextureCalibration`

**Files:**
- Modify: `src/Mithril.MapCalibration/AreaCalibration.cs`
- Modify: `src/Mithril.MapCalibration/WorldToTextureCalibration.cs`
- Modify: `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`
- Modify: `tests/Mithril.MapCalibration.Tests/WorldToTextureCalibrationTests.cs` (extend)
- Create: `tests/Mithril.MapCalibration.Tests/AreaCalibrationTextureShaTests.cs`

- [ ] **Step 1: Write the failing test for round-trip + absent-field default**

Create `tests/Mithril.MapCalibration.Tests/AreaCalibrationTextureShaTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1081 — AreaCalibration grows a PixelSha256 field carrying the
/// base texture's identity. AutoCal stamps at solve time; the overlay
/// uses it to look up dims in the canonical-asset-hash catalogue. Pre-#1081
/// records load with PixelSha256 = null and fail-soft at the render path.
/// </summary>
public sealed class AreaCalibrationTextureShaTests
{
    [Fact]
    public void PixelSha256_RoundTripThroughJson()
    {
        var record = new AreaCalibration(
            Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
            ReferenceCount: 5, ResidualPixels: 0.5)
        {
            Source = CalibrationSource.AutoCapture,
            Frame = CalibrationFrame.Texture,
            PixelSha256 = "abc123def",
        };

        var json = JsonSerializer.Serialize(record, MapCalibrationJsonContext.Default.AreaCalibration);

        json.Should().Contain("\"pixelSha256\":\"abc123def\"");

        var roundTrip = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration);
        roundTrip!.PixelSha256.Should().Be("abc123def");
    }

    [Fact]
    public void AbsentPixelSha256_DeserialiseAsNull()
    {
        // Pre-#1081 records omit pixelSha256. STJ should default to null,
        // which the overlay's compose helper short-circuits to "no render."
        var preStampJson = """
            {
              "scale": 1.0,
              "rotationRadians": 0.0,
              "originX": 0.0,
              "originY": 0.0,
              "referenceCount": 5,
              "residualPixels": 0.5,
              "source": "AutoCapture",
              "frame": "Texture"
            }
            """;

        var deserialised = JsonSerializer.Deserialize(preStampJson, MapCalibrationJsonContext.Default.AreaCalibration);

        deserialised!.PixelSha256.Should().BeNull();
    }

    [Fact]
    public void UnknownFutureField_IgnoredWhenLoading()
    {
        // Forward-compat — STJ ignores unknown fields next to PixelSha256.
        var futureJson = """
            {
              "scale": 1.0, "rotationRadians": 0.0, "originX": 0.0, "originY": 0.0,
              "referenceCount": 5, "residualPixels": 0.5,
              "source": "AutoCapture", "frame": "Texture",
              "pixelSha256": "abc123",
              "futureFieldX": "ignored"
            }
            """;

        var deserialised = JsonSerializer.Deserialize(futureJson, MapCalibrationJsonContext.Default.AreaCalibration);

        deserialised!.PixelSha256.Should().Be("abc123");
    }
}
```

- [ ] **Step 2: Run to verify failure**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~AreaCalibrationTextureShaTests" -v minimal
```

Expected: FAIL — `AreaCalibration` has no `PixelSha256` property.

- [ ] **Step 3: Add `PixelSha256` to `AreaCalibration`**

Edit `src/Mithril.MapCalibration/AreaCalibration.cs` — add after the existing `Frame` property:

```csharp
/// <summary>
/// SHA-256 (lowercase hex) of the base texture this calibration was solved
/// against — same digest the sidecar's MapTextureManifest carries and the
/// CanonicalAssetHashGate checks. Stamped at AutoCal-solve time
/// (mithril#1081) and on bundled-baseline rows at commit time. Identifies
/// WHICH texture the math is bound to; the overlay derives the texture's
/// pixel dimensions by looking this up via
/// <see cref="IMapTextureDimensions"/>. Null on records persisted before
/// #1081 → unrenderable on the overlay (drift-check unaffected — it doesn't
/// need dims). Overlay-frame records leave this null; they don't compose
/// against a texture.
/// </summary>
public string? PixelSha256 { get; init; }
```

- [ ] **Step 4: Run tests for the record-side change**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~AreaCalibrationTextureShaTests" -v minimal
```

Expected: PASS (3/3).

- [ ] **Step 5: Write the failing test for struct-side mirroring**

Append to `tests/Mithril.MapCalibration.Tests/WorldToTextureCalibrationTests.cs`:

```csharp
[Fact]
public void PixelSha256_CarryThroughTheStruct()
{
    // mithril#1081 — the texture identity travels with the typed projection
    // struct, not just the AreaCalibration record. The overlay's per-frame
    // compose reads the struct (via IMapCalibrationService.GetTextureCalibration)
    // and uses its PixelSha256 to look up dims for ProjectThroughOverlay.
    var cal = new WorldToTextureCalibration(
        OriginX: 0, OriginY: 0, Scale: 1.0,
        RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0)
    {
        PixelSha256 = "abc123",
    };

    cal.PixelSha256.Should().Be("abc123");
}
```

- [ ] **Step 6: Run to verify failure**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~WorldToTextureCalibrationTests.PixelSha256_CarryThroughTheStruct" -v minimal
```

Expected: FAIL — `WorldToTextureCalibration` has no `PixelSha256`.

- [ ] **Step 7: Add `PixelSha256` to `WorldToTextureCalibration`**

Edit `src/Mithril.MapCalibration/WorldToTextureCalibration.cs` — add after the existing `SchemaVersion` property (line 23):

```csharp
/// <summary>
/// SHA-256 (lowercase hex) of the base texture this calibration was solved
/// against. Mirrors <see cref="AreaCalibration.PixelSha256"/> — see that
/// doc. Read by the Legolas overlay (mithril#1081) to look up the
/// texture's native pixel dimensions via <see cref="IMapTextureDimensions"/>
/// when composing through <see cref="ProjectThroughOverlay(MapRect)"/>.
/// </summary>
public string? PixelSha256 { get; init; }
```

- [ ] **Step 8: Thread `PixelSha256` through `ToTextureCalibration` in `MapCalibrationService`**

Find `ToTextureCalibration(AreaCalibration cal)` in `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs` (search for `ToTextureCalibration(`). Add the field:

```csharp
private static WorldToTextureCalibration ToTextureCalibration(AreaCalibration cal) =>
    new(cal.OriginX, cal.OriginY, cal.Scale, cal.RotationRadians,
        cal.MirrorNorth, cal.CalibrationZoom)
    {
        PixelSha256 = cal.PixelSha256,
    };
```

If the existing method already populates other init-only fields, add `PixelSha256` alongside them.

- [ ] **Step 9: Run all calibration core tests**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests -v minimal
```

Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/Mithril.MapCalibration/AreaCalibration.cs src/Mithril.MapCalibration/WorldToTextureCalibration.cs src/Mithril.MapCalibration/Internal/MapCalibrationService.cs tests/Mithril.MapCalibration.Tests/AreaCalibrationTextureShaTests.cs tests/Mithril.MapCalibration.Tests/WorldToTextureCalibrationTests.cs
git commit -m "$(cat <<'EOF'
feat(map-calibration): add PixelSha256 to AreaCalibration + WorldToTextureCalibration (#1081)

Texture identity field on the calibration record + the typed projection
struct. Additive JSON (STJ defaults absent fields to null); pre-#1081
records load with PixelSha256 = null and fail-soft at the overlay's
compose helper (catalogue lookup short-circuits). ToTextureCalibration
threads the field from record to struct.

AutoCal stamping in the next task.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: AutoCal stamps `PixelSha256` at solve time

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` (line ~717)
- Create: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineShaStampTests.cs` (or extend an existing engine-tests file if the project's pattern is to consolidate)

- [ ] **Step 1: Locate the existing post-solve persistence block**

Open `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`. The block ~line 717:

```csharp
var stamped = result.Calibration with
{
    Source = CalibrationSource.AutoCapture,
    Frame = CalibrationFrame.Texture,
};
```

`baseTexture` (a `GrayImage`) is in scope (resolved at line 520).

- [ ] **Step 2: Write the failing test**

The existing engine tests in `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` use a fixture pattern around `RunAttemptAsync`. Locate that pattern by reading the test file's existing `Persisted: true` assertions. Add a new fact (or create a sibling test file if the existing class is sealed/closed):

```csharp
[Fact]
public async Task PersistedCalibration_CarriesBaseTexturePixelSha256()
{
    // mithril#1081 — AutoCal stamps the base texture's SHA-256 onto the
    // persisted AreaCalibration so the Legolas overlay can look up dims
    // via IMapTextureDimensions when composing through ProjectThroughOverlay.
    // The sha matches the same digest the sidecar's MapTextureManifest
    // carries (same gray pixels → same hash).
    var fixtureBaseTextureBytes = /* the fixture's gray pixels */;
    var expectedSha = Convert.ToHexStringLower(SHA256.HashData(fixtureBaseTextureBytes));

    var fixture = AutoCalibrationEngineTestFixture.NewWithBaseTextureBytes(fixtureBaseTextureBytes);
    var (engine, calibrationService) = fixture.Build();

    var outcome = await engine.RunAttemptAsync(/* canonical scene + capture */);

    outcome.Persisted.Should().BeTrue();
    var persisted = calibrationService.GetCalibration(fixture.Scene);
    persisted!.PixelSha256.Should().Be(expectedSha);
}
```

**Adapt to the project's actual fixture shape.** Search `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs` and `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` for the existing `RunAttemptAsync` driver. Mirror its pattern; the new fact's only requirement is "persisted record's `PixelSha256` equals `SHA256(baseTexture.Pixels)`."

If the existing fixtures don't expose a way to assert on the persisted-record sha, add a thin extension method or helper that pulls the persisted `AreaCalibration` from the fake calibration service and reads `.PixelSha256`.

- [ ] **Step 3: Run to verify failure**

```pwsh
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~PersistedCalibration_CarriesBaseTexturePixelSha256" -v minimal
```

Expected: FAIL — persisted record's `PixelSha256` is null.

- [ ] **Step 4: Add the stamp**

Edit the `var stamped = result.Calibration with` block at line 717:

```csharp
// mithril#1081: stamp the base texture's SHA-256 so the Legolas overlay
// can look up dims via IMapTextureDimensions when composing the record
// onto the overlay surface. Same digest the sidecar's MapTextureManifest
// carries; we re-hash from baseTexture.Pixels (~1 MB at 1024², sub-ms)
// rather than threading it through IBaseTextureProvider.
var stamped = result.Calibration with
{
    Source = CalibrationSource.AutoCapture,
    Frame = CalibrationFrame.Texture,
    PixelSha256 = Convert.ToHexStringLower(SHA256.HashData(baseTexture.Pixels)),
};
```

Add `using System.Security.Cryptography;` at the top of the file (if not already present).

- [ ] **Step 5: Run to verify pass**

```pwsh
dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~PersistedCalibration_CarriesBaseTexturePixelSha256" -v minimal
```

Expected: PASS.

- [ ] **Step 6: Run the full engine test suite to confirm no regression**

```pwsh
dotnet test tests/Mithril.MapCalibration.Capture.Tests -v minimal
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs tests/Mithril.MapCalibration.Capture.Tests/
git commit -m "$(cat <<'EOF'
feat(map-calibration): AutoCal stamps PixelSha256 on persisted records (#1081)

One-line addition at the post-solve `stamped = result.Calibration with`
block: re-hash baseTexture.Pixels with SHA256 (sub-millisecond at 1024²),
stamp PixelSha256 in lowercase hex. Same digest the sidecar's
MapTextureManifest carries, so the overlay can look up dims via
IMapTextureDimensions in the next task.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Populate the bundled catalogue + stamp `pixelSha256` on bundled-baseline rows

**Files:**
- Modify: `src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json`
- Modify: `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`
- Create: `tests/Mithril.MapCalibration.Tests/BundledCatalogueLintTests.cs`

This task is partly data-authoring and partly a lint guard. The lint test fails-build on any inconsistency between the two bundled files.

- [ ] **Step 1: Write the failing lint test**

Create `tests/Mithril.MapCalibration.Tests/BundledCatalogueLintTests.cs`:

```csharp
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1081 — invariants on the bundled-data files:
///
/// <list type="number">
/// <item>Every texture-frame baseline row carries a non-null PixelSha256
/// that resolves to a positive-dim entry in the catalogue. A row added
/// without a matching catalogue entry would render empty on the overlay
/// (catalogue miss); the build fails fast at #1081 commit time.</item>
/// <item>The catalogue carries no sha-collision-with-conflicting-dims. The
/// same sha (= same pixel content) must always carry the same dims; a
/// mismatch is a bundling bug (re-harvest went wrong).</item>
/// </list>
/// </summary>
public sealed class BundledCatalogueLintTests
{
    private static CanonicalAssetHashes LoadCatalogue()
    {
        var asm = typeof(CatalogueMapTextureDimensions).Assembly;
        using var stream = asm.GetManifestResourceStream("Mithril.MapCalibration.BundledData.canonical-asset-hashes.json")
            ?? throw new InvalidOperationException("catalogue resource missing");
        return CanonicalAssetHashesLoader.TryLoad(stream, NullLogger.Instance)
            ?? throw new InvalidOperationException("catalogue resource failed to parse");
    }

    [Fact]
    public void EveryTextureFrameBaseline_HasPixelSha256_ResolvingInCatalogue()
    {
        var baseline = BundledBaselineLoader.Load(NullLogger.Instance);
        var catalogue = LoadCatalogue();
        var dims = new CatalogueMapTextureDimensions(catalogue);

        var textureRows = baseline
            .Where(kv => kv.Value.Frame == CalibrationFrame.Texture)
            .ToList();

        textureRows.Should().NotBeEmpty(
            "BundledBaselineLoader stamps Frame=Texture on every row by construction.");

        foreach (var (key, cal) in textureRows)
        {
            cal.PixelSha256.Should().NotBeNullOrWhiteSpace(
                $"bundled row {key} must carry PixelSha256 — see " +
                $"docs/planning/calibration-1081-overlay-cross-frame-composition/spec.md §4.3");

            var resolved = dims.TryGetSizeBySha(cal.PixelSha256);
            resolved.Should().NotBeNull(
                $"bundled row {key}'s PixelSha256 ({cal.PixelSha256}) must resolve in the catalogue " +
                $"with positive dims. A miss means the catalogue is missing this scene's entry — " +
                $"add it under the current PG version's byPgVersion section.");
        }
    }

    [Fact]
    public void Catalogue_HasNoConflictingShaDims()
    {
        var catalogue = LoadCatalogue();

        // Collect every (sha, w, h) tuple; ensure same sha never carries conflicting dims.
        var seen = new Dictionary<string, (int W, int H, string Origin)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pg, byArtifact) in catalogue.ByPgVersion)
        {
            foreach (var (key, entry) in byArtifact)
            {
                if (entry.Width <= 0 || entry.Height <= 0) continue;  // v1-wrapped; skip
                var origin = $"byPgVersion[\"{pg}\"][\"{key}\"]";
                if (seen.TryGetValue(entry.Sha, out var prior))
                {
                    (entry.Width, entry.Height).Should().Be((prior.W, prior.H),
                        $"sha {entry.Sha} appears at {prior.Origin} with ({prior.W}x{prior.H}) " +
                        $"AND at {origin} with ({entry.Width}x{entry.Height}). Same pixel content " +
                        $"must yield the same dims; this is a bundling bug — re-harvest from the " +
                        $"sidecar.");
                }
                else
                {
                    seen[entry.Sha] = (entry.Width, entry.Height, origin);
                }
            }
        }
    }
}
```

- [ ] **Step 2: Run to verify failure (test 1) + initial pass-shape (test 2)**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~BundledCatalogueLintTests" -v minimal
```

Expected:
- Test 1 (`EveryTextureFrameBaseline_HasPixelSha256_ResolvingInCatalogue`): FAIL — bundled rows have null `PixelSha256`.
- Test 2 (`Catalogue_HasNoConflictingShaDims`): PASS on the empty stub (no entries → no conflicts possible).

- [ ] **Step 3: Source the texture dims + shas for the bundled-baseline scenes**

The bundled-baseline currently ships 3 rows: `Map_AreaSerbule`, `Map_AreaEltibule`, `Map_AreaKurMountains`. Each needs:
- `pixelSha256`: SHA-256 of the gray-pixel payload of the canonical base texture.
- `width` / `height`: native dims.

**Authoritative source: the sidecar's per-asset manifest.** Procedure:

1. On a known-good PG install at the current supported PG version (read this with `cdn_version` MCP if needed; the version string is what `_pgVersion` would resolve to at runtime).
2. Run the asset-extractor sidecar against each of the three scenes. The sidecar invocation is documented in [`tools/Mithril.AssetExtractor/README.md`](../../../tools/Mithril.AssetExtractor/README.md); the command shape is roughly:

   ```pwsh
   dotnet run --project tools/Mithril.AssetExtractor -- --install-root "<PG_INSTALL_PATH>" --out-dir "$env:LocalAppData\Mithril\assets" --kind Texture --map-asset Map_AreaSerbule
   ```

3. The sidecar emits `%LocalAppData%/Mithril/assets/map-texture-Map_AreaSerbule.json`. Open it:

   ```pwsh
   Get-Content "$env:LocalAppData\Mithril\assets\map-texture-Map_AreaSerbule.json"
   ```

   It contains `pixelSha256`, `width`, `height` (plus `pgVersion` and `extractorVersion`). Record those three values.

4. Repeat for `Map_AreaEltibule` and `Map_AreaKurMountains`.

If any of these steps is blocked in the worktree (no PG install accessible, sidecar can't run, etc.), **block the task and request the data from the user**: list the three `(MapAssetKey, pgVersion, sha, w, h)` tuples needed. Do not invent values — the bundled catalogue must reflect real sidecar output or the renderer will silently misproject.

- [ ] **Step 4: Populate `canonical-asset-hashes.json`**

Replace the contents of `src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json`:

```json
{
  "schemaVersion": 2,
  "byPgVersion": {
    "<PG_VERSION>": {
      "Map_AreaSerbule": {
        "sha": "<SHA_SERBULE>",
        "width": <W_SERBULE>,
        "height": <H_SERBULE>
      },
      "Map_AreaEltibule": {
        "sha": "<SHA_ELTIBULE>",
        "width": <W_ELTIBULE>,
        "height": <H_ELTIBULE>
      },
      "Map_AreaKurMountains": {
        "sha": "<SHA_KUR>",
        "width": <W_KUR>,
        "height": <H_KUR>
      }
    }
  }
}
```

Substitute the four placeholders with the values from Step 3. The inner-key format is `Map_<X>` per the hash gate's existing call convention (verified at `CachedBaseTextureProvider.cs:94`).

- [ ] **Step 5: Stamp `pixelSha256` on each bundled-baseline row**

Edit `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`. For each anchor entry, add `pixelSha256` next to the existing fields:

```json
"Map_AreaSerbule": {
  "scale": 0.8225888770409359,
  "rotationRadians": 7.088147823900868E-05,
  "originX": -159.67286441908084,
  "originY": 2271.6816745475235,
  "referenceCount": 8,
  "residualPixels": 0.3030622255943075,
  "source": "BundledBaseline",
  "pixelSha256": "<SHA_SERBULE>"
}
```

Repeat for `Map_AreaEltibule` and `Map_AreaKurMountains`. Use the same SHA values as the catalogue (they MUST match — that's exactly what the lint test enforces).

Do NOT bump `schemaVersion` in the baseline file; this is additive JSON on `AreaCalibration` (handled by STJ default-tolerance).

- [ ] **Step 6: Run the lint tests**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~BundledCatalogueLintTests" -v minimal
```

Expected: PASS (2/2).

- [ ] **Step 7: Run the full Mithril.MapCalibration.Tests suite**

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests -v minimal
```

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Mithril.MapCalibration/BundledData tests/Mithril.MapCalibration.Tests/BundledCatalogueLintTests.cs
git commit -m "$(cat <<'EOF'
feat(map-calibration): populate v2 catalogue + stamp baseline pixelSha256 (#1081)

canonical-asset-hashes.json bumped to schemaVersion 2, populated with
{ sha, width, height } for the three bundled-baseline scenes
(Map_AreaSerbule, Map_AreaEltibule, Map_AreaKurMountains) at the current
PG version. Each entry sourced from the sidecar's per-asset manifest
output.

map-calibration-baseline.json gains pixelSha256 per row (matching the
catalogue's sha values).

New lint test asserts every texture-frame baseline row resolves in the
catalogue with positive dims, and no sha appears twice with conflicting
dims (same pixel content = same dims invariant).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: `ResolveComposedOverlayCalibration` pure helper + decision-table

**Files:**
- Modify: `src/Mithril.Overlay/Internal/OverlayWindowService.cs`
- Create: `tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs`

This is the load-bearing logic of #1081. Decision-table coverage is non-negotiable.

- [ ] **Step 1: Write the failing decision-table test**

Create `tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs`:

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// mithril#1081 — the per-frame compose helper resolves an effective
/// WorldToOverlayCalibration? for the current scene, either directly from
/// an overlay-frame record or by composing a texture-frame record onto the
/// overlay surface size via WorldToTextureCalibration.ProjectThroughOverlay.
/// Dims are content-addressed via IMapTextureDimensions (cal.PixelSha256
/// lookup); failure modes are: null sha (pre-#1081), catalogue miss
/// (uncatalogued / newer PG), unsized surface (first frame after Show).
/// </summary>
public sealed class ResolveComposedOverlayCalibrationTests
{
    private static readonly MapSceneRef Scene =
        new(ParentAreaKey: "AreaTest", SceneFriendlyName: null, MapAssetKey: "Map_Test");

    private const string KnownSha = "abc123def";

    private static WorldToOverlayCalibration MakeOverlayCal() =>
        new(OriginX: 100, OriginY: 200, Scale: 1.0,
            RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0);

    private static WorldToTextureCalibration MakeTexCal(string? sha = KnownSha) =>
        new(OriginX: 50, OriginY: 75, Scale: 2.0,
            RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0)
        {
            PixelSha256 = sha,
        };

    private sealed class StubDims : IMapTextureDimensions
    {
        public (int W, int H)? Result { get; set; }
        public (int Width, int Height)? TryGetSizeBySha(string? sha) => Result;
    }

    private static StubDims DimsWith(int w, int h) => new() { Result = (w, h) };
    private static StubDims DimsNull() => new() { Result = null };

    [Fact]
    public void WizardOnly_ReturnsDirectOverlayCal()
    {
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: MakeOverlayCal(),
            textureCal: null,
            dims: DimsNull(),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().NotBeNull();
        path.Should().Be(OverlayWindowService.CalPath.DirectOverlay);
        cal!.Value.OriginX.Should().Be(100);
    }

    [Fact]
    public void AutoCalOnly_ShaInCatalogue_ReturnsComposedFromTexture()
    {
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: null,
            textureCal: MakeTexCal(),
            dims: DimsWith(1024, 1024),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().NotBeNull();
        path.Should().Be(OverlayWindowService.CalPath.ComposedFromTexture);
    }

    [Fact]
    public void AutoCalOnly_NullSha_ReturnsNone()
    {
        // Pre-#1081 record.
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: null,
            textureCal: MakeTexCal(sha: null),
            dims: DimsWith(1024, 1024),  // catalogue knows things, but cal has no sha
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().BeNull();
        path.Should().Be(OverlayWindowService.CalPath.None);
    }

    [Fact]
    public void AutoCalOnly_ShaNotInCatalogue_ReturnsNone()
    {
        // Newer PG patch than catalogue, or uncatalogued asset.
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: null,
            textureCal: MakeTexCal(),
            dims: DimsNull(),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().BeNull();
        path.Should().Be(OverlayWindowService.CalPath.None);
    }

    [Fact]
    public void AutoCalOnly_UnsizedSurface_ReturnsNone()
    {
        // First frame after Show(); ActualWidth/Height not yet laid out.
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: null,
            textureCal: MakeTexCal(),
            dims: DimsWith(1024, 1024),
            surfaceWidth: 0, surfaceHeight: 0);

        cal.Should().BeNull();
        path.Should().Be(OverlayWindowService.CalPath.None);
    }

    [Fact]
    public void BothFramesPresent_PrefersDirectOverlay()
    {
        // Per #1082's per-frame slots, both records can exist; the overlay
        // takes the direct-overlay path, composition is dead code.
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: MakeOverlayCal(),
            textureCal: MakeTexCal(),
            dims: DimsWith(1024, 1024),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().NotBeNull();
        path.Should().Be(OverlayWindowService.CalPath.DirectOverlay);
        cal!.Value.OriginX.Should().Be(100);
    }

    [Fact]
    public void Uncalibrated_ReturnsNone()
    {
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: null,
            textureCal: null,
            dims: DimsNull(),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().BeNull();
        path.Should().Be(OverlayWindowService.CalPath.None);
    }

    [Fact]
    public void NullScene_ReturnsNone()
    {
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: null,
            overlayCal: null,
            textureCal: null,
            dims: DimsNull(),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().BeNull();
        path.Should().Be(OverlayWindowService.CalPath.None);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```pwsh
dotnet test tests/Mithril.Overlay.Tests --filter "FullyQualifiedName~ResolveComposedOverlayCalibrationTests" -v minimal
```

Expected: FAIL — `OverlayWindowService.ResolveComposedOverlayCalibrationForTest` doesn't exist.

- [ ] **Step 3: Add the pure helper + `CalPath` enum on `OverlayWindowService`**

Edit `src/Mithril.Overlay/Internal/OverlayWindowService.cs`. Add inside the class body (near the existing `ProjectMarkers` static helper):

```csharp
/// <summary>
/// mithril#1081 — three render-side outcomes for resolving a usable
/// <see cref="WorldToOverlayCalibration"/> for the current scene. Surfaced
/// as the <c>cal.path</c> tag on the <c>project</c> span.
/// </summary>
internal enum CalPath
{
    /// <summary>No usable cal this frame (uncalibrated, null-sha cal,
    /// catalogue miss, or surface unsized).</summary>
    None,
    /// <summary>An overlay-frame record exists; consumed directly.</summary>
    DirectOverlay,
    /// <summary>Only a texture-frame record exists; composed onto the
    /// overlay surface via
    /// <see cref="WorldToTextureCalibration.ProjectThroughOverlay(MapRect)"/>
    /// with dims looked up from <see cref="IMapTextureDimensions"/>.</summary>
    ComposedFromTexture,
}

/// <summary>
/// mithril#1081 — pure helper, exposed for unit tests. The decision table
/// is the load-bearing logic of #1081; production calls go through
/// <see cref="ResolveComposedOverlayCalibration(MapSceneRef?)"/> which reads
/// the service's <see cref="_calibration"/> + <see cref="_textureDimensions"/>
/// + the live overlay surface. This overload takes the inputs directly so
/// the table can be exercised without standing up the service.
/// </summary>
internal static (WorldToOverlayCalibration? Cal, CalPath Path)
    ResolveComposedOverlayCalibrationForTest(
        MapSceneRef? scene,
        WorldToOverlayCalibration? overlayCal,
        WorldToTextureCalibration? textureCal,
        IMapTextureDimensions dims,
        double surfaceWidth,
        double surfaceHeight)
{
    if (scene is null) return (null, CalPath.None);

    // Prefer an overlay-frame record when present — direct path. Per
    // mithril#1082's per-frame slots, this is both the wizard-only case
    // AND the both-frames-present case; the texture-frame composition is
    // dead code on the latter.
    if (overlayCal is not null)
        return (overlayCal, CalPath.DirectOverlay);

    if (textureCal is null) return (null, CalPath.None);
    var tex = textureCal.Value;

    // F1 — pre-#1081 record with no stamped sha. Renderer skips; user
    // recovers by re-running AutoCalibrate.
    if (string.IsNullOrWhiteSpace(tex.PixelSha256)) return (null, CalPath.None);

    // F2 — overlay surface not yet laid out (first frame after Show()).
    if (surfaceWidth <= 0 || surfaceHeight <= 0) return (null, CalPath.None);

    // F5 (the invalidation case) and F2 catalogue-miss collapse here.
    var resolved = dims.TryGetSizeBySha(tex.PixelSha256);
    if (resolved is not { } d) return (null, CalPath.None);

    var overlayRect = new MapRect(
        OriginX: 0, OriginY: 0,
        Width: (int)surfaceWidth, Height: (int)surfaceHeight,
        TextureWidth: d.Width, TextureHeight: d.Height);

    return (tex.ProjectThroughOverlay(overlayRect), CalPath.ComposedFromTexture);
}
```

Add `using Mithril.MapCalibration;` to the top of the file if not already present.

- [ ] **Step 4: Run tests to verify pass**

```pwsh
dotnet test tests/Mithril.Overlay.Tests --filter "FullyQualifiedName~ResolveComposedOverlayCalibrationTests" -v minimal
```

Expected: PASS (8/8).

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Overlay/Internal/OverlayWindowService.cs tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs
git commit -m "$(cat <<'EOF'
feat(overlay): add ResolveComposedOverlayCalibration helper + decision table (#1081)

Pure decision-table helper that returns a usable WorldToOverlayCalibration?
for the current scene plus a CalPath enum tagging the resolution path
(DirectOverlay / ComposedFromTexture / None). Dims content-addressed via
IMapTextureDimensions using the cal's stamped PixelSha256. Eight decision-
table facts cover the full state space.

Production wiring follows in the next task.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Production wiring — `OnSurfaceRender` resolves once, threads through `BeginFrame`

**Files:**
- Modify: `src/Mithril.Overlay/Internal/OverlayWindowService.cs`
- Modify: `src/Mithril.Overlay/Internal/OverlayWindow.xaml.cs` (expose `OverlaySurface` accessor if not already)

- [ ] **Step 1: Verify the `OverlayWindow.OverlaySurface` accessor**

Open `src/Mithril.Overlay/Internal/OverlayWindow.xaml.cs`. The XAML names the child element `Surface` (see line 79 of `OverlayWindow.xaml`); the code-behind partial has a generated `Surface` field at the XAML compile step. Add a public accessor matching what the service will read:

```csharp
// mithril#1081 — the overlay service reads the surface's ActualWidth/Height
// at the top of OnSurfaceRender to build the MapRect for cross-frame
// composition. The XAML-generated field is `Surface`; the accessor names
// the surface explicitly so the service doesn't depend on the codegen name.
internal D2DOverlaySurface OverlaySurface => Surface;
```

If `OverlayWindow` doesn't already have a code-behind file with an `internal` member surface, add the accessor as a property. The `internal` access matches `Mithril.Overlay.Internal.OverlayWindowService`'s namespace.

- [ ] **Step 2: Inject `IMapTextureDimensions` into `OverlayWindowService`**

In `src/Mithril.Overlay/Internal/OverlayWindowService.cs`, add the field:

```csharp
private readonly IMapTextureDimensions _textureDimensions;
```

Add the constructor parameter and assignment:

```csharp
public OverlayWindowService(
    WorldOverlayMarkers markers,
    MarkerSceneRenderer renderer,
    IMapCalibrationService calibration,
    IAreaState areaState,
    IMapState mapState,
    ISceneAssetCache sceneCache,
    IDomainEventSubscriber bus,
    IPositionState positionState,
    IOverlayZoomSource zoomSource,
    IMapTextureDimensions textureDimensions,   // new
    ILoggerFactory? loggerFactory = null)
{
    _markers = markers;
    _renderer = renderer;
    _calibration = calibration;
    _areaState = areaState;
    _mapState = mapState;
    _sceneCache = sceneCache;
    _bus = bus;
    _positionState = positionState;
    _zoomSource = zoomSource;
    _textureDimensions = textureDimensions;     // new
    _loggerFactory = loggerFactory;
    _logger = loggerFactory?.CreateLogger("Mithril.Overlay");
    _sceneContext = new OverlaySceneContext(this);
}
```

The `IMapTextureDimensions` was registered in Task 4 in core's DI extensions. The shell composes both registrations and resolves this constructor automatically.

- [ ] **Step 3: Add the production overload of `ResolveComposedOverlayCalibration`**

Add the instance method alongside the pure helper from Task 8:

```csharp
/// <summary>
/// mithril#1081 — production overload. Reads the active calibration via
/// <see cref="_calibration"/>, the texture-dim catalogue via
/// <see cref="_textureDimensions"/>, and the overlay surface's live size,
/// then delegates to the pure helper.
/// </summary>
private (WorldToOverlayCalibration? Cal, CalPath Path)
    ResolveComposedOverlayCalibration(MapSceneRef? scene)
{
    if (scene is not { } s) return (null, CalPath.None);
    var overlayCal = _calibration.GetOverlayCalibration(s);
    var textureCal = overlayCal is null ? _calibration.GetTextureCalibration(s) : null;
    var (w, h) = ResolveOverlaySurfaceSize();
    return ResolveComposedOverlayCalibrationForTest(s, overlayCal, textureCal, _textureDimensions, w, h);
}

/// <summary>
/// Read the live overlay surface's DIU size. Per the overlay's strict-1:1
/// invariant (docs/legolas-overview.md §Pitfalls + the XAML's no-internal-
/// zoom-pan promise) the surface fills the in-game map region; ActualWidth/
/// Height in DIU == OverlayPixel in current Mithril (single-monitor DPI;
/// CanvasOverlayMapping identity case per #1077 §3 / P.3). Returns (0,0)
/// when the window or surface isn't realised yet — caller treats as F2
/// fail-soft.
/// </summary>
private (double Width, double Height) ResolveOverlaySurfaceSize()
{
    var window = _window;
    if (window is null) return (0, 0);
    var surface = window.OverlaySurface;
    if (surface is null) return (0, 0);
    return (surface.ActualWidth, surface.ActualHeight);
}
```

- [ ] **Step 4: Thread `composedCal` through `OverlaySceneContext.BeginFrame`**

In the nested `OverlaySceneContext` private class (~line 656), add the field:

```csharp
private WorldToOverlayCalibration? _composedCal;
```

Update `BeginFrame`:

```csharp
public void BeginFrame(
    ID2D1RenderTarget renderTarget,
    ID2D1Factory factory,
    D2DBrushCache brushes,
    string areaKey,
    MapSceneRef scene,
    double currentZoom,
    WorldToOverlayCalibration? composedCal)   // new
{
    _renderTarget = renderTarget;
    _factory = factory;
    _brushes = brushes;
    _areaKey = areaKey;
    _scene = scene;
    _currentZoom = currentZoom;
    _composedCal = composedCal;
}
```

Replace `OverlaySceneContext.Project` (around line 700-713):

```csharp
public OverlayPixel? Project(double worldX, double worldZ) =>
    _composedCal?.ToOverlay(new WorldCoord(worldX, 0, worldZ), _currentZoom);
```

The previous `_owner._calibration.WorldToOverlay(...)` call (line 711) goes away — it's replaced by the bound-cal read above.

- [ ] **Step 5: Update `OnSurfaceRender` to resolve + pass through the composed cal**

In `OnSurfaceRender`, after the `currentZoom = SnapshotZoom()` call (around line 359), insert:

```csharp
// mithril#1081 — resolve the per-frame composed overlay-frame calibration
// once, then thread it through to scene drawers (via BeginFrame) and to the
// marker loop. Replaces the per-call calibration.WorldToOverlay path the
// marker loop and IOverlaySceneContext.Project used to take individually.
var (composedCal, calPath) = ResolveComposedOverlayCalibration(resolvedScene);
```

Update the existing `BeginFrame` call (around line 377) to pass `composedCal`:

```csharp
_sceneContext.BeginFrame(e.RenderTarget, e.Factory, _brushCache, areaKey, frameScene, currentZoom, composedCal);
```

Also update the test-seam `DriveSceneForTest` (around line 517) to pass `null` for `composedCal`:

```csharp
_sceneContext.BeginFrame(renderTarget, factory, _brushCache, areaKey, scene, currentZoom, composedCal: null);
```

(The test seam's existing callers don't exercise the composed-cal path. Task 11 adds the integration fact that DOES.)

- [ ] **Step 6: Build to verify wiring compiles**

```pwsh
dotnet build src/Mithril.Overlay -v minimal
```

Expected: success.

- [ ] **Step 7: Update test `BuildService` helpers to compile with the new constructor arg**

Adding the `textureDimensions` constructor parameter breaks every test that constructs an `OverlayWindowService`. Locate `BuildService` (or equivalent) in `tests/Mithril.Overlay.Tests/`:

```pwsh
dotnet build tests/Mithril.Overlay.Tests -v minimal
```

Compiler errors will list every call site. For each, pass a no-op `IMapTextureDimensions` so existing tests keep their behaviour (null dims → composed-from-texture path returns null, which matches the prior null-projection-on-uncalibrated behaviour):

```csharp
// In tests/Mithril.Overlay.Tests/OverlaySceneHookTests.cs (and any
// sibling helper file), update the existing BuildService helper:
private static OverlayWindowService BuildService(
    FakeMapCalibrationService calibration, IAreaState areaState, IOverlayZoomSource zoom)
{
    return new OverlayWindowService(
        markers: /* existing */,
        renderer: /* existing */,
        calibration: calibration,
        areaState: areaState,
        mapState: /* existing */,
        sceneCache: /* existing */,
        bus: /* existing */,
        positionState: /* existing */,
        zoomSource: zoom,
        textureDimensions: new NullMapTextureDimensions(),   // new
        loggerFactory: null);
}

// Add the helper alongside the test class (or in a Fakes file):
private sealed class NullMapTextureDimensions : IMapTextureDimensions
{
    public (int Width, int Height)? TryGetSizeBySha(string? sha) => null;
}
```

Apply to every `BuildService` call site (likely just the one shared helper, but the build errors are authoritative).

- [ ] **Step 8: Run all Overlay tests**

```pwsh
dotnet test tests/Mithril.Overlay.Tests -v minimal
```

Expected: most pass; the `Project_plumbs_current_zoom_into_WorldToOverlay` test in `OverlaySceneHookTests.cs` will fail because it asserts on the retired `IMapCalibrationService.WorldToOverlay` per-call path. Task 11 fixes it.

The DI graph: shell composition resolves `IMapTextureDimensions` for the `OverlayWindowService` constructor. Task 4 already registered it in core's DI extensions; verify the shell's composition root reaches that registration.

- [ ] **Step 9: Commit**

```bash
git add src/Mithril.Overlay/Internal/OverlayWindowService.cs src/Mithril.Overlay/Internal/OverlayWindow.xaml.cs
git commit -m "$(cat <<'EOF'
feat(overlay): per-frame compose threaded through BeginFrame + Project (#1081)

OnSurfaceRender resolves the composed WorldToOverlayCalibration? once
per frame via ResolveComposedOverlayCalibration (which consults the
calibration service AND IMapTextureDimensions) and threads it to
OverlaySceneContext via BeginFrame. Project reads the bound cal;
the per-marker calibration.WorldToOverlay path is gone from the scene-
drawer surface.

OverlaySceneHookTests.Project_plumbs_current_zoom_into_WorldToOverlay
fails because it asserts on the retired per-call path; Task 11 adapts
it to the new bound-cal seam.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Reshape `ProjectMarkers` to take the bound composed cal

**Files:**
- Modify: `src/Mithril.Overlay/Internal/OverlayWindowService.cs`
- Modify: existing tests that call `ProjectMarkers` (locate via build errors)

- [ ] **Step 1: Reshape `ProjectMarkers` to take a `WorldToOverlayCalibration?` directly**

In `OverlayWindowService.cs`, replace both overloads of `ProjectMarkers` (around line 434):

```csharp
/// <summary>Pure projection helper — takes a snapshot + a composed
/// overlay-frame calibration and returns the projected pixel list. The
/// composed cal is resolved once per frame by
/// <see cref="ResolveComposedOverlayCalibration"/>; this helper has no
/// dependency on <see cref="IMapCalibrationService"/>. Test-friendly
/// overload.</summary>
internal static IReadOnlyList<(OverlayPixel Pixel, IMarkerStyle Style)> ProjectMarkers(
    IReadOnlyList<MarkerSnapshot> markers,
    WorldToOverlayCalibration? composedCal,
    double currentZoom)
    => ProjectMarkers(markers, composedCal, currentZoom, onMiss: null, snapshotCount: markers.Count);

private static IReadOnlyList<(OverlayPixel Pixel, IMarkerStyle Style)> ProjectMarkers(
    IReadOnlyList<MarkerSnapshot> markers,
    WorldToOverlayCalibration? composedCal,
    double currentZoom,
    OverlayWindowService? onMiss,
    int snapshotCount)
{
    if (markers.Count == 0 || composedCal is null)
    {
        // mithril#1081: per-scene miss telemetry fires at the
        // OnSurfaceRender level (cal.path = none), not per marker.
        return Array.Empty<(OverlayPixel, IMarkerStyle)>();
    }

    var result = new List<(OverlayPixel, IMarkerStyle)>(markers.Count);
    var cal = composedCal.Value;
    for (var i = 0; i < markers.Count; i++)
    {
        var snap = markers[i];
        result.Add((cal.ToOverlay(snap.World, currentZoom), snap.Style));
    }
    return result;
}
```

- [ ] **Step 2: Update the call site in `OnSurfaceRender`**

Around line 397, change:

```csharp
var projected = ProjectMarkers(snapshot, resolvedScene!.Value, _calibration, currentZoom,
    onMiss: this, snapshotCount: snapshot.Count);
```

to:

```csharp
var projected = ProjectMarkers(snapshot, composedCal, currentZoom,
    onMiss: this, snapshotCount: snapshot.Count);
```

The `onMiss` callback fires once per scene when `composedCal` is null (handled at the top of the helper); the per-marker miss-telemetry simplifies.

- [ ] **Step 3: Build to verify compile**

```pwsh
dotnet build src/Mithril.Overlay -v minimal
```

Expected: success.

- [ ] **Step 4: Run Overlay tests to find call-site breakage**

```pwsh
dotnet test tests/Mithril.Overlay.Tests -v minimal
```

Expected: build errors at tests that called the old `ProjectMarkers(markers, scene, fakeCal, zoom)` signature. Identify each via the compiler output.

- [ ] **Step 5: Update each broken test call site**

For each test invoking `ProjectMarkers` with the old shape, change:

```csharp
// before
var result = OverlayWindowService.ProjectMarkers(markers, scene, fakeCal, currentZoom: 1.0);
```

to:

```csharp
// after — pass the composed cal that the helper would produce
var composed = new WorldToOverlayCalibration(
    OriginX: 0, OriginY: 0, Scale: 1.0,
    RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0);
var result = OverlayWindowService.ProjectMarkers(markers, composed, currentZoom: 1.0);
```

For tests that assert on the *null-projection* path, pass `composedCal: null` instead. Match the existing tests' expected pixel outcomes by picking `WorldToOverlayCalibration` parameters that reproduce them.

- [ ] **Step 6: Run again to verify pass**

```pwsh
dotnet test tests/Mithril.Overlay.Tests -v minimal
```

Expected: PASS (apart from `Project_plumbs_current_zoom_into_WorldToOverlay`, which Task 11 fixes).

- [ ] **Step 7: Commit**

```bash
git add src/Mithril.Overlay tests/Mithril.Overlay.Tests
git commit -m "$(cat <<'EOF'
refactor(overlay): ProjectMarkers takes the bound composed cal (#1081)

ProjectMarkers no longer participates in calibration resolution — it
takes a WorldToOverlayCalibration? directly, resolved once per frame
by OnSurfaceRender via ResolveComposedOverlayCalibration. Per-marker
null-skip collapses into the top-level guard; per-scene miss telemetry
fires once at the OnSurfaceRender level.

Existing tests adapted to the new signature.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Adapt `Project_plumbs_current_zoom` + add texture-frame integration fact

**Files:**
- Modify: `tests/Mithril.Overlay.Tests/Fakes/FakeMapCalibrationService.cs` (add hookable cal returns)
- Modify: `tests/Mithril.Overlay.Tests/OverlaySceneHookTests.cs`

- [ ] **Step 1: Add hookable cal returns on `FakeMapCalibrationService`**

Open `tests/Mithril.Overlay.Tests/Fakes/FakeMapCalibrationService.cs`. The existing fake stubs `GetTextureCalibration` to return null (verified at line 30). Replace with hookable versions:

```csharp
public Func<MapSceneRef, WorldToOverlayCalibration?>? OverlayCalForScene { get; set; }
public Func<MapSceneRef, WorldToTextureCalibration?>? TextureCalForScene { get; set; }

public WorldToOverlayCalibration? GetOverlayCalibration(MapSceneRef scene)
{
    if (OverlayCalForScene is { } hook) return hook(scene);
    return CalibratedAreas.Contains(scene.MapAssetKey)
        ? new WorldToOverlayCalibration(
            OriginX: 0, OriginY: 0, Scale: 1.0,
            RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0)
        : null;
}

public WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene)
    => TextureCalForScene?.Invoke(scene);
```

The default (no hook) preserves existing behaviour: scenes in `CalibratedAreas` are calibrated via an identity overlay-frame cal; texture-frame is null. Tests that need either path inject via the hooks.

- [ ] **Step 2: Replace `Project_plumbs_current_zoom_into_WorldToOverlay` with the bound-cal version**

The old test asserted that `IMapCalibrationService.WorldToOverlay` is called with the live zoom. The new shape: the scene context's `Project` reads the bound `_composedCal` and passes `_currentZoom` to `composedCal.ToOverlay`. Verify by checking the projected `OverlayPixel` differs as zoom changes:

```csharp
[Fact]
public void Project_plumbs_current_zoom_into_bound_composed_cal()
{
    var calibration = new FakeMapCalibrationService();
    calibration.CalibratedAreas.Add("Map_A");
    // Use a cal whose Scale × zoom-ratio is observable: Scale=10, CalibrationZoom=1.0,
    // so ToOverlay's output scales linearly with the per-tick zoom.
    calibration.OverlayCalForScene = _ =>
        new WorldToOverlayCalibration(
            OriginX: 0, OriginY: 0, Scale: 10.0,
            RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0);

    var areaState = new StubAreaState { CurrentArea = "Map_A" };
    var zoom = new MutableZoomSource(1.5);
    var service = BuildService(calibration, areaState, zoom);

    var projectedPoints = new List<OverlayPixel?>();
    using var h = ((IOverlayWindow)service).RegisterScene(ctx =>
    {
        projectedPoints.Add(ctx.Project(10, 20));
    });

    service.DriveSceneForTest(null!, null!, "Map_A", 1.5);
    var firstAtZoom1_5 = projectedPoints[^1];

    zoom.CurrentZoom = 0.75;
    service.DriveSceneForTest(null!, null!, "Map_A", 0.75);
    var secondAtZoom0_75 = projectedPoints[^1];

    firstAtZoom1_5.Should().NotBe(secondAtZoom0_75,
        because: "Project must pass the per-tick live zoom into the bound " +
        "WorldToOverlayCalibration.ToOverlay call. If this regresses to a hardcoded " +
        "zoom (or the bound cal's CalibrationZoom only), pins drift whenever the " +
        "in-game zoom slider is off the calibration zoom. mithril#1081 moved the " +
        "seam from IMapCalibrationService.WorldToOverlay to OverlaySceneContext's " +
        "bound _composedCal, but the live-zoom invariant from PR #863 remains.");
}
```

Delete the obsolete `Project_plumbs_current_zoom_into_WorldToOverlay` test (the assertion is fully subsumed by this new fact).

- [ ] **Step 3: Add the texture-frame composition integration fact**

Per spec §6 (Integration: end-to-end through `DriveSceneForTest`), confirm the composed-from-texture path works at the public scene-drawer surface:

```csharp
[Fact]
public void Project_composes_texture_frame_when_only_AutoCal_record_exists()
{
    // mithril#1081 — Project must succeed for a scene whose only calibration
    // is texture-frame (AutoCal-produced). Without ProjectThroughOverlay
    // composition + IMapTextureDimensions dim resolution, the scene-drawer
    // surface returns null for every pin → markers silently miss → user sees
    // empty overlay despite "calibrated" chip.
    var calibration = new FakeMapCalibrationService();
    calibration.CalibratedAreas.Add("Map_A");
    calibration.OverlayCalForScene = _ => null;
    calibration.TextureCalForScene = _ =>
        new WorldToTextureCalibration(
            OriginX: 0, OriginY: 0, Scale: 1.0,
            RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0)
        {
            PixelSha256 = "test-sha",
        };

    var stubDims = new StubMapTextureDimensions((1000, 1000));

    var areaState = new StubAreaState { CurrentArea = "Map_A" };
    var service = BuildService(calibration, areaState,
        new FixedOverlayZoomSource(1.0), stubDims);
    // BuildService grows an optional 4th parameter in Step 4.

    var projected = new List<OverlayPixel?>();
    using var h = ((IOverlayWindow)service).RegisterScene(ctx =>
    {
        projected.Add(ctx.Project(100, 200));
    });

    service.DriveSceneForTest(null!, null!, "Map_A", 1.0);

    projected.Single().Should().NotBeNull(
        because: "Project must compose the texture-frame record onto the overlay " +
        "surface via WorldToTextureCalibration.ProjectThroughOverlay with dims " +
        "from IMapTextureDimensions. A null return here means #1081's composition " +
        "path is broken or the overlay-surface size lookup returned 0.");
}

private sealed class StubMapTextureDimensions(
    (int W, int H)? result) : IMapTextureDimensions
{
    public (int Width, int Height)? TryGetSizeBySha(string? sha) => result;
}
```

- [ ] **Step 4: Extend `BuildService` to take an optional `IMapTextureDimensions` parameter**

Task 9 Step 7 already updated `BuildService` to pass a `NullMapTextureDimensions`. Rework it to accept the dims as a parameter (defaulting to the no-op) so the new texture-frame fact can inject `StubMapTextureDimensions((1000, 1000))`:

```csharp
private static OverlayWindowService BuildService(
    FakeMapCalibrationService calibration,
    IAreaState areaState,
    IOverlayZoomSource zoom,
    IMapTextureDimensions? dims = null)
{
    return new OverlayWindowService(
        // … existing args …
        zoomSource: zoom,
        textureDimensions: dims ?? new NullMapTextureDimensions(),
        loggerFactory: null);
}
```

All existing call sites continue to work (default null → `NullMapTextureDimensions`). The new fact at Step 3 calls `BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0), stubDims)`. Remove the temporary `BuildServiceWithDims` reference from Step 3's code block (the test calls `BuildService` with the four-arg overload).

**Caveat.** If `DriveSceneForTest` doesn't realize a non-zero overlay-surface size (the test seam may not initialize the WPF surface), `Project_composes_texture_frame_when_only_AutoCal_record_exists` cannot pass without the surface size > 0. Inspect what `BuildService` + `DriveSceneForTest` do. If the surface is null/unrealised at this seam, mark the new fact:

```csharp
[Fact(Skip = "DriveSceneForTest doesn't realize the overlay surface; composed-from-texture path is exercised through ResolveComposedOverlayCalibrationTests (Task 8) which covers the decision table without a live surface.")]
```

…rather than passing silently on a null projection. The decision-table coverage in Task 8 is the substantive coverage; this integration fact is a belt for that suspenders.

- [ ] **Step 5: Run the full Overlay test suite**

```pwsh
dotnet test tests/Mithril.Overlay.Tests -v minimal
```

Expected: PASS (all). The original `Project_plumbs_current_zoom_into_WorldToOverlay` is gone; the new `Project_plumbs_current_zoom_into_bound_composed_cal` passes.

- [ ] **Step 6: Commit**

```bash
git add tests/Mithril.Overlay.Tests
git commit -m "$(cat <<'EOF'
test(overlay): adapt zoom-plumbing test + add texture-compose integration (#1081)

Replace Project_plumbs_current_zoom_into_WorldToOverlay (asserted on the
retired IMapCalibrationService.WorldToOverlay seam) with the bound-cal
version. Same invariant; new seam.

Add Project_composes_texture_frame_when_only_AutoCal_record_exists to
exercise the texture-frame composition path through DriveSceneForTest.
Skipped if the test seam doesn't realize the overlay surface — the
decision-table coverage in Task 8 is the substantive net.

FakeMapCalibrationService gets hookable OverlayCalForScene /
TextureCalForScene functions so tests can inject per-frame cal records;
BuildServiceWithDims helper threads IMapTextureDimensions through the
test rig.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Span tag `cal.path` + perf-trace-schema doc

**Files:**
- Modify: `src/Mithril.Overlay/Internal/OverlayWindowService.cs` (the existing `project` span around line 408)
- Modify: `docs/perf-trace-schema.md`

- [ ] **Step 1: Tag the existing `project` span with `cal.path`**

In `OnSurfaceRender`, inside the existing `using (var renderAct = MithrilActivitySources.Overlay.StartActivity("project"))` block (around line 408), add:

```csharp
renderAct?.SetTag("area", areaKey);
renderAct?.SetTag("scene.asset_key", resolvedScene!.Value.MapAssetKey);
renderAct?.SetTag("marker_count", projected.Count);
renderAct?.SetTag("cal.path", calPath switch
{
    CalPath.DirectOverlay => "direct_overlay",
    CalPath.ComposedFromTexture => "composed_from_texture",
    _ => "none",
});  // mithril#1081 — observable in perf-recorder JSONL
_renderer.Render(projected, e.RenderTarget, e.Factory, _brushCache);
```

`calPath` is in scope from Task 9 (the tuple destructuring at the top of `OnSurfaceRender`).

- [ ] **Step 2: Document the tag in `docs/perf-trace-schema.md`**

Open `docs/perf-trace-schema.md`. Locate the section for the Overlay `project` span (search for `"project"`). Add a row for `cal.path`:

```markdown
| `cal.path` | string | One of `direct_overlay` (overlay-frame record consumed directly), `composed_from_texture` (texture-frame record composed via `WorldToTextureCalibration.ProjectThroughOverlay(MapRect)` — dims looked up from `IMapTextureDimensions` by `cal.PixelSha256`; see #1081), `none` (no usable calibration this frame: uncalibrated scene, null-sha cal, catalogue miss, or overlay surface not laid out yet). |
```

Match the existing table's formatting.

- [ ] **Step 3: Build + smoke test**

```pwsh
dotnet build src/Mithril.Overlay -v minimal
dotnet test tests/Mithril.Overlay.Tests -v minimal
```

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.Overlay/Internal/OverlayWindowService.cs docs/perf-trace-schema.md
git commit -m "$(cat <<'EOF'
feat(overlay): tag project span with cal.path resolution outcome (#1081)

Three values — direct_overlay, composed_from_texture, none — tag the
existing project span so perf-recorder JSONL distinguishes the three
render-side outcomes per frame. Document in perf-trace-schema.md.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 13: Full-suite build + verification

- [ ] **Step 1: Build the whole solution**

```pwsh
dotnet build Mithril.slnx -v minimal
```

Expected: success, no errors.

- [ ] **Step 2: Run the whole test suite**

```pwsh
dotnet test Mithril.slnx -v minimal
```

Expected: PASS (all).

Likely-culprit failures if anything regresses:
- A fixture in `Mithril.MapCalibration.Capture.Tests` constructing `AreaCalibration` positional + `with`-syntax that didn't expect `PixelSha256` — should be additive-safe (defaults to null), but if a test asserts on exact JSON shape it may catch the new field. Update the assertion.
- A fixture in `tests/Mithril.MapCalibration.Tests/Detection/CanonicalAssetHashGateTests.cs` that wasn't updated in Task 2 — fix per Task 2 Step 6.
- DI graph regression in the shell test (`tests/Mithril.Shell.Tests/`) where `IMapTextureDimensions` isn't registered — verify Task 4's registration is picked up by the shell's composition root.

- [ ] **Step 3: Manual in-game smoke**

Verification step, not code. Mark complete after the in-game pass succeeds:

1. Start a fresh profile (or wipe `%LocalAppData%\Mithril\MapCalibration\refinements.json`).
2. Launch the shell. Visit `AreaSerbule` (or another bundled-baseline scene). The bundled-baseline texture-frame record + the catalogue entry both lie ready.
3. Confirm the Legolas overlay renders markers correctly. `cal.path` span tag should be `composed_from_texture` (verify via perf-recorder JSONL).
4. Run the Legolas wizard on the same scene to produce an overlay-frame `UserRefinement`. After Confirm, the overlay renders via direct-overlay path (`cal.path` = `direct_overlay`).
5. Hand-edit `refinements.json` to corrupt a texture-frame entry's `pixelSha256` (e.g. flip a character). Restart. The overlay surfaces no markers for that scene; `cal.path` = `none`; `MithrilMeters.Overlay.ProjectionMisses` increments.
6. Hand-edit `canonical-asset-hashes.json` to remove the bundled scene's entry. Restart. Overlay skips markers for that scene; `cal.path` = `none` (catalogue miss).

---

## Task 14: PR creation

- [ ] **Step 1: Push branch + open PR**

```bash
git push -u origin claude/affectionate-feistel-353af1
gh pr create --title "Legolas overlay cross-frame composition (closes #1081)" --body "$(cat <<'EOF'
## Summary

- Stamp `PixelSha256` on `AreaCalibration` / `WorldToTextureCalibration` at AutoCal solve time; bundled-baseline rows hand-stamped from the same harvest source.
- Extend `CanonicalAssetHashes` v1→v2 to carry `{ sha, width, height }` per entry; the same canonical-asset-hashes.json catalogue serves both the hash gate (reads `.Sha`) and the new dim resolver (reads `.Width/.Height`).
- Lift catalogue types from `Mithril.MapCalibration.Detection` to core so `Mithril.Overlay` (which only depends on core) can consume the dim slice via the new `IMapTextureDimensions` service.
- `OverlayWindowService.OnSurfaceRender` resolves a composed `WorldToOverlayCalibration?` once per frame — direct overlay-frame record OR composed-from-texture via `WorldToTextureCalibration.ProjectThroughOverlay(MapRect)` with dims content-addressed by sha — and threads it through `BeginFrame` to scene drawers and marker projection. Calibration service no longer on the per-marker render path.
- New `cal.path` span tag on the `project` span tagging the three outcomes (`direct_overlay` / `composed_from_texture` / `none`); documented in `docs/perf-trace-schema.md`.

Closes the last AutoCalibration release blocker per [#1077 spec §12](docs/planning/calibration-1076-pixel-frame-typing/spec.md). Spec: [docs/planning/calibration-1081-overlay-cross-frame-composition/spec.md](docs/planning/calibration-1081-overlay-cross-frame-composition/spec.md).

## Test plan

- [ ] `dotnet test Mithril.slnx` — all green
- [ ] Decision-table coverage on `ResolveComposedOverlayCalibration`: 8 facts in `tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs`
- [ ] Bundled-catalogue lint test: every texture-frame baseline row resolves in the catalogue with positive dims; no sha-collision-with-conflicting-dims
- [ ] JSON round-trip + absent-`pixelSha256`-defaults-to-null fixtures
- [ ] Catalogue v1→v2 backwards-compat loader test (v1 wraps to zero-dim entries)
- [ ] In-game smoke per spec §10: AutoCal-only scene renders via `composed_from_texture`; both-frame scene takes `direct_overlay`; corrupted-sha record drops to `none`; catalogue-removed entry drops to `none`

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 2: Flip the INDEX row to point at the PR**

After the PR opens, edit `docs/planning/INDEX.md` to add the PR link to the calibration-1081 row's Issue/PR cell.

```bash
# After editing INDEX.md
git add docs/planning/INDEX.md
git commit -m "docs(planning): link #1081 PR on the INDEX row"
git push
```

---

## Verification-owed (carried from spec §10)

These belong on the PR's verification checklist, not as plan tasks. Surface in the PR body after the test plan:

- [x] **Hash-gate inner-key format** — verified during plan drafting: hash gate is called with `Map_<X>` format (`CachedBaseTextureProvider.cs:94`). Catalogue inner-key uses `Map_<X>`.
- [ ] **Bundled-baseline `pixelSha256` coverage** — automated by `BundledCatalogueLintTests` (Task 7)
- [ ] **Picker tiebreak on a scene with both frames** — automated by `ResolveComposedOverlayCalibrationTests.BothFramesPresent_PrefersDirectOverlay` (Task 8)
- [ ] **End-to-end manual verification before AutoCal GA ships** — manual, per Task 13 Step 3
