# VM projection paths → composed-cal migration — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate 7 VM-side `CurrentOverlayCalibration` reads (4 in `MapOverlayViewModel`, 1 in `PlayerLogIngestionService`, 1 in `CalibrationSessionViewModel`, plus the toggle-log read-through in `SetCalibrationValidation`) onto a shared `IComposedOverlayCalibrationResolver` so texture-frame-only scenes (post-#1081 AutoCal records with no overlay-frame record) project pink dots, motherlode markers, survey pins, and wizard ghost landmarks — parity with the marker-projection block in `OverlayWindowService`.

**Architecture:** A new `IComposedOverlayCalibrationResolver` service in `Mithril.Overlay` lifts the existing `OverlayWindowService.ResolveComposedOverlayCalibrationForTest` pure helper (8-case decision table already proven). `IOverlayWindow` gains `GetSurfaceSize()` returning live D2D overlay surface dims. VM consumers route every overlay-cal read through a single private `ResolveOverlayCal()` helper that delegates to the composer when wired (production / new tests) and falls back to legacy `_areaCalibration.CurrentOverlayCalibration` direct-only behaviour when not wired (preserves every existing test).

**Tech Stack:** C# 13 / .NET 10, WPF, xunit + FluentAssertions, `System.Diagnostics.ActivitySource` / `System.Diagnostics.Metrics.Meter`. Project layering: `Mithril.Overlay` references `Mithril.MapCalibration`, `Legolas.Module` references both.

**Spec:** [spec.md](spec.md). **Issue:** [#1096](https://github.com/moumantai-gg/mithril/issues/1096).

---

## File Structure

**Create:**

- `src/Mithril.Overlay/CalPath.cs` — public enum (lifted from `OverlayWindowService` internal).
- `src/Mithril.Overlay/ComposedCalResolution.cs` — public readonly record struct returned by the resolver.
- `src/Mithril.Overlay/IComposedOverlayCalibrationResolver.cs` — public interface.
- `src/Mithril.Overlay/Internal/ComposedOverlayCalibrationResolver.cs` — internal impl, takes `IMapCalibrationService` + `IMapTextureDimensions`. Body lifts the existing pure helper + classifier.

**Modify:**

- `src/Mithril.Overlay/IOverlayWindow.cs` — add `(double Width, double Height) GetSurfaceSize()`.
- `src/Mithril.Overlay/Internal/OverlayWindowService.cs` — implement `GetSurfaceSize()` (lift `ResolveOverlaySurfaceSize`). Replace `ResolveComposedOverlayCalibration` / `ResolveComposedOverlayCalibrationForTest` / `ClassifyComposedMissReason` with a thin call into the injected resolver. Ctor gains `IComposedOverlayCalibrationResolver` parameter.
- `src/Mithril.Overlay/DependencyInjection/OverlayServiceCollectionExtensions.cs` — register `IComposedOverlayCalibrationResolver`.
- `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs` — ctor adds optional `IComposedOverlayCalibrationResolver` + `IOverlayWindow`. New private `ResolveOverlayCal()` helper. 4 call sites + the `SetCalibrationValidation` toggle-log read-through use the helper.
- `src/Legolas.Module/Services/PlayerLogIngestionService.cs` — ctor adds optional composer + overlay window; `HandleMapTarget` uses them.
- `src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs` — ctor adds optional composer; `ProjectLandmarks` reads `_viewportW` / `_viewportH` and calls composer.
- `src/Legolas.Module/LegolasModule.cs` — DI registration extends MapOverlayViewModel + PlayerLogIngestionService + CalibrationSessionViewModel factories to resolve the new dependencies.
- `src/Legolas.Module/Diagnostics/LegolasCalibrationTagDescriptors.cs:45` — `cal.path` doc-comment value list: `direct_overlay | composed_from_texture | none`.
- `docs/perf-trace-schema.md` — add `composed_from_texture` value to the `cal.path` row; document the `MissReason` snake_case vocabulary; note the `ProjectionSkipped` semantic change.
- `docs/planning/INDEX.md` — flip status `active` → `shipped` after manual verify.

**Rename:**

- `tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs` → `ComposedOverlayCalibrationResolverTests.cs`. Replace `OverlayWindowService.ResolveComposedOverlayCalibrationForTest(...)` calls with composer instance calls. Extend each None-returning case with a `MissReason` assertion.

**Add a single integration test:**

- `tests/Legolas.Tests/ViewModels/MapOverlayComposedCalMigrationTests.cs` — headline behaviour: texture-frame-only record + sized surface → `CalibrationGhosts.Count > 0`.

---

## Task 0: Public `CalPath` + `ComposedCalResolution` + resolver interface

**Files:**
- Create: `src/Mithril.Overlay/CalPath.cs`
- Create: `src/Mithril.Overlay/ComposedCalResolution.cs`
- Create: `src/Mithril.Overlay/IComposedOverlayCalibrationResolver.cs`

- [ ] **Step 1: Create `CalPath.cs`**

Write `src/Mithril.Overlay/CalPath.cs`:

```csharp
namespace Mithril.Overlay;

/// <summary>How a usable <see cref="Mithril.MapCalibration.WorldToOverlayCalibration"/>
/// was resolved for the current scene. Surfaced as the <c>cal.path</c> tag on
/// calibration consumer spans (post-#1093). Public so cross-project producers
/// (Legolas VM paths, OverlayWindowService) emit the same vocabulary.
///
/// <para>mithril#1096: lifted from <c>OverlayWindowService</c> internal so the
/// VM-side consumer chain can emit the same enum values that
/// <see cref="IComposedOverlayCalibrationResolver"/> returns.</para></summary>
public enum CalPath
{
    /// <summary>No usable cal this frame (uncalibrated, null-sha cal,
    /// catalogue miss, or surface unsized). The companion
    /// <see cref="ComposedCalResolution.MissReason"/> names which sub-case.</summary>
    None,

    /// <summary>An overlay-frame record exists; consumed directly.</summary>
    DirectOverlay,

    /// <summary>Only a texture-frame record exists; composed onto the
    /// overlay surface via
    /// <see cref="Mithril.MapCalibration.WorldToTextureCalibration.ProjectThroughOverlay(Mithril.MapCalibration.MapRect)"/>
    /// with dims looked up from
    /// <see cref="Mithril.MapCalibration.IMapTextureDimensions"/>.</summary>
    ComposedFromTexture,
}
```

- [ ] **Step 2: Create `ComposedCalResolution.cs`**

Write `src/Mithril.Overlay/ComposedCalResolution.cs`:

```csharp
using Mithril.MapCalibration;

namespace Mithril.Overlay;

/// <summary>Result of <see cref="IComposedOverlayCalibrationResolver.Resolve"/>.
/// On success, <see cref="Calibration"/> is non-null and <see cref="Path"/> says
/// how it was resolved. On miss, <see cref="Path"/> is <see cref="CalPath.None"/>
/// and <see cref="MissReason"/> carries a stable, lowercase, snake_case reason
/// suitable for feeding into <c>LogCalibrationFallback</c>'s dedup key.
///
/// <para>MissReason vocabulary (post-#1096):
/// <list type="bullet">
/// <item><c>no_scene</c> — caller passed null <c>scene</c>.</item>
/// <item><c>no_usable_calibration</c> — picker returned neither overlay-frame
/// nor texture-frame record.</item>
/// <item><c>null_sha</c> — texture-frame record exists but <c>PixelSha256</c>
/// is null (pre-#1081 record; user recovers by re-running AutoCalibrate).</item>
/// <item><c>unsized_surface</c> — surface dims ≤ 0 (window not yet realised;
/// first frame after <c>Show()</c>; wizard viewport not laid out).</item>
/// <item><c>catalogue_miss</c> — texture-frame sha doesn't match any entry in
/// the bundled <c>CanonicalAssetHashes</c>.</item>
/// </list></para></summary>
public readonly record struct ComposedCalResolution(
    WorldToOverlayCalibration? Calibration,
    CalPath Path,
    string? MissReason);
```

- [ ] **Step 3: Create `IComposedOverlayCalibrationResolver.cs`**

Write `src/Mithril.Overlay/IComposedOverlayCalibrationResolver.cs`:

```csharp
using Arda.Contracts;
using Mithril.MapCalibration;

namespace Mithril.Overlay;

/// <summary>Composes a <see cref="WorldToOverlayCalibration"/> for an
/// arbitrary surface size by reading <see cref="IMapCalibrationService"/>'s
/// frame-typed records: an overlay-frame record consumes directly; a
/// texture-frame record composes onto the surface rect via
/// <see cref="WorldToTextureCalibration.ProjectThroughOverlay(MapRect)"/>
/// with dims looked up from <see cref="IMapTextureDimensions"/>.
///
/// <para>Pure: the (scene, w, h) inputs fully determine the result given the
/// injected calibration + dim-catalogue state. The caller chooses the
/// surface (overlay window vs. wizard canvas vs. test).</para>
///
/// <para>mithril#1096: lifted from <c>OverlayWindowService.ResolveComposedOverlayCalibrationForTest</c>
/// so VM-side consumers + the marker projection block can share one resolver
/// and emit one <c>cal.path</c> vocabulary.</para></summary>
public interface IComposedOverlayCalibrationResolver
{
    /// <summary>Resolve the composed overlay calibration for <paramref name="scene"/>
    /// against a target surface of the given dimensions. See
    /// <see cref="ComposedCalResolution"/> for the result shape.</summary>
    ComposedCalResolution Resolve(MapSceneRef? scene, double surfaceWidth, double surfaceHeight);
}
```

- [ ] **Step 4: Run build to verify the new files compile**

Run: `dotnet build src/Mithril.Overlay/Mithril.Overlay.csproj`
Expected: build succeeds (the types are unused so far, but they reference real existing types).

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Overlay/CalPath.cs src/Mithril.Overlay/ComposedCalResolution.cs src/Mithril.Overlay/IComposedOverlayCalibrationResolver.cs
git commit -m "$(cat <<'EOF'
mithril#1096 — Add public CalPath enum + ComposedCalResolution + resolver interface

Public surface for the composed-cal migration. The enum and result record live
in Mithril.Overlay so cross-project producers (Legolas VM paths, OverlayWindowService)
emit the same vocabulary. Resolver impl + DI registration land in the next commit.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 1: Implement `ComposedOverlayCalibrationResolver`

**Files:**
- Create: `src/Mithril.Overlay/Internal/ComposedOverlayCalibrationResolver.cs`

- [ ] **Step 1: Write the impl by lifting `ResolveComposedOverlayCalibrationForTest` + `ClassifyComposedMissReason`**

Write `src/Mithril.Overlay/Internal/ComposedOverlayCalibrationResolver.cs`:

```csharp
using Arda.Contracts;
using Mithril.MapCalibration;

namespace Mithril.Overlay.Internal;

/// <summary>Default <see cref="IComposedOverlayCalibrationResolver"/>. Body
/// lifted verbatim from <c>OverlayWindowService.ResolveComposedOverlayCalibrationForTest</c>
/// + <c>ClassifyComposedMissReason</c> (the 8-case decision table already
/// proven by <c>ResolveComposedOverlayCalibrationTests</c>).</summary>
internal sealed class ComposedOverlayCalibrationResolver : IComposedOverlayCalibrationResolver
{
    private readonly IMapCalibrationService _calibration;
    private readonly IMapTextureDimensions _textureDimensions;

    public ComposedOverlayCalibrationResolver(
        IMapCalibrationService calibration,
        IMapTextureDimensions textureDimensions)
    {
        _calibration = calibration;
        _textureDimensions = textureDimensions;
    }

    public ComposedCalResolution Resolve(MapSceneRef? scene, double surfaceWidth, double surfaceHeight)
    {
        if (scene is not { } s)
            return new(null, CalPath.None, "no_scene");

        // Prefer an overlay-frame record when present — direct path.
        var overlayCal = _calibration.GetOverlayCalibration(s);
        if (overlayCal is not null)
            return new(overlayCal, CalPath.DirectOverlay, null);

        var textureCal = _calibration.GetTextureCalibration(s);
        if (textureCal is null)
            return new(null, CalPath.None, "no_usable_calibration");

        var tex = textureCal.Value;

        // F1 — pre-#1081 record with no stamped sha. User recovers by re-running AutoCalibrate.
        if (string.IsNullOrWhiteSpace(tex.PixelSha256))
            return new(null, CalPath.None, "null_sha");

        // F2 — surface not yet laid out.
        if (surfaceWidth <= 0 || surfaceHeight <= 0)
            return new(null, CalPath.None, "unsized_surface");

        var resolved = _textureDimensions.TryGetSizeBySha(tex.PixelSha256);
        if (resolved is not { } d)
            return new(null, CalPath.None, "catalogue_miss");

        var overlayRect = new MapRect(
            OriginX: 0, OriginY: 0,
            Width: (int)surfaceWidth, Height: (int)surfaceHeight,
            TextureWidth: d.Width, TextureHeight: d.Height);

        return new(tex.ProjectThroughOverlay(overlayRect), CalPath.ComposedFromTexture, null);
    }
}
```

- [ ] **Step 2: Register the resolver in DI**

Edit `src/Mithril.Overlay/DependencyInjection/OverlayServiceCollectionExtensions.cs`. Add inside `AddMithrilOverlay`, right after the `MarkerSceneRenderer` registration block (before the `OverlayWindowService` registration):

```csharp
// mithril#1096 — composed-cal resolver lifted from OverlayWindowService internal.
// Shared by VM consumers (Legolas) + OverlayWindowService (parity).
services.TryAddSingleton<IComposedOverlayCalibrationResolver, ComposedOverlayCalibrationResolver>();
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Mithril.Overlay/Mithril.Overlay.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.Overlay/Internal/ComposedOverlayCalibrationResolver.cs src/Mithril.Overlay/DependencyInjection/OverlayServiceCollectionExtensions.cs
git commit -m "$(cat <<'EOF'
mithril#1096 — Implement ComposedOverlayCalibrationResolver + DI registration

Body lifted verbatim from OverlayWindowService.ResolveComposedOverlayCalibrationForTest
+ ClassifyComposedMissReason. The 8-case decision table is the same — the
existing ResolveComposedOverlayCalibrationTests still cover it (rewired in
the next commit).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Rewire `ResolveComposedOverlayCalibrationTests` to the resolver + assert `MissReason`

**Files:**
- Modify: `tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs` → rename to `ComposedOverlayCalibrationResolverTests.cs`

- [ ] **Step 1: Rename the test file**

```bash
git mv tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs tests/Mithril.Overlay.Tests/ComposedOverlayCalibrationResolverTests.cs
```

- [ ] **Step 2: Rewrite the file to use the resolver interface + assert MissReason**

Replace the entire contents of `tests/Mithril.Overlay.Tests/ComposedOverlayCalibrationResolverTests.cs` with:

```csharp
using Arda.Contracts;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// mithril#1096 — IComposedOverlayCalibrationResolver covers the same 8-case
/// decision table as the pre-#1096 OverlayWindowService internal helper, plus
/// MissReason vocabulary assertions for the None-returning cases.
///
/// (Pre-#1096 history: this file was ResolveComposedOverlayCalibrationTests
/// targeting OverlayWindowService.ResolveComposedOverlayCalibrationForTest;
/// the 8 cases are preserved verbatim — only the call shape changed.)
/// </summary>
public sealed class ComposedOverlayCalibrationResolverTests
{
    private static readonly MapSceneRef Scene =
        new(ParentAreaKey: "AreaTest", SceneFriendlyName: null, MapAssetKey: "Map_Test");

    private const string KnownSha = "abc123def";

    private static WorldToOverlayCalibration MakeOverlayCal() =>
        new(OriginX: 100, OriginY: 200, Scale: 1.0,
            RotationRadians: 0, MirrorNorth: false);

    private static WorldToTextureCalibration MakeTexCal(string? sha = KnownSha) =>
        new(OriginX: 50, OriginY: 75, Scale: 2.0,
            RotationRadians: 0, MirrorNorth: false)
        {
            PixelSha256 = sha,
        };

    private sealed class StubCal : IMapCalibrationService
    {
        public WorldToOverlayCalibration? OverlayCal { get; set; }
        public WorldToTextureCalibration? TextureCal { get; set; }

        public WorldToOverlayCalibration? GetOverlayCalibration(MapSceneRef scene) => OverlayCal;
        public WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene) => TextureCal;
        public AreaCalibration? GetCalibration(MapSceneRef scene) => null;
        public bool IsCalibrated(MapSceneRef scene) => OverlayCal is not null || TextureCal is not null;
        public event EventHandler<MapSceneRef>? Changed { add { } remove { } }
        public void Upsert(MapSceneRef scene, AreaCalibration calibration) { }
        public void Clear(MapSceneRef scene) { }
    }

    private sealed class StubDims : IMapTextureDimensions
    {
        public (int W, int H)? Result { get; set; }
        public (int Width, int Height)? TryGetSizeBySha(string? sha) => Result;
    }

    private static IComposedOverlayCalibrationResolver Make(
        WorldToOverlayCalibration? overlayCal = null,
        WorldToTextureCalibration? textureCal = null,
        (int W, int H)? dims = null)
        => new ComposedOverlayCalibrationResolver(
            new StubCal { OverlayCal = overlayCal, TextureCal = textureCal },
            new StubDims { Result = dims });

    [Fact]
    public void WizardOnly_ReturnsDirectOverlayCal()
    {
        var r = Make(overlayCal: MakeOverlayCal()).Resolve(Scene, 800, 600);

        r.Calibration.Should().NotBeNull();
        r.Path.Should().Be(CalPath.DirectOverlay);
        r.MissReason.Should().BeNull();
        r.Calibration!.Value.OriginX.Should().Be(100);
    }

    [Fact]
    public void AutoCalOnly_ShaInCatalogue_ReturnsComposedFromTexture()
    {
        var r = Make(textureCal: MakeTexCal(), dims: (1024, 1024)).Resolve(Scene, 800, 600);

        r.Calibration.Should().NotBeNull();
        r.Path.Should().Be(CalPath.ComposedFromTexture);
        r.MissReason.Should().BeNull();
    }

    [Fact]
    public void AutoCalOnly_NullSha_ReturnsNone_NullSha()
    {
        var r = Make(textureCal: MakeTexCal(sha: null), dims: (1024, 1024)).Resolve(Scene, 800, 600);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("null_sha");
    }

    [Fact]
    public void AutoCalOnly_ShaNotInCatalogue_ReturnsNone_CatalogueMiss()
    {
        var r = Make(textureCal: MakeTexCal(), dims: null).Resolve(Scene, 800, 600);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("catalogue_miss");
    }

    [Fact]
    public void AutoCalOnly_UnsizedSurface_ReturnsNone_UnsizedSurface()
    {
        var r = Make(textureCal: MakeTexCal(), dims: (1024, 1024)).Resolve(Scene, 0, 0);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("unsized_surface");
    }

    [Fact]
    public void BothFramesPresent_PrefersDirectOverlay()
    {
        var r = Make(overlayCal: MakeOverlayCal(), textureCal: MakeTexCal(), dims: (1024, 1024))
            .Resolve(Scene, 800, 600);

        r.Calibration.Should().NotBeNull();
        r.Path.Should().Be(CalPath.DirectOverlay);
        r.MissReason.Should().BeNull();
        r.Calibration!.Value.OriginX.Should().Be(100);
    }

    [Fact]
    public void Uncalibrated_ReturnsNone_NoUsableCalibration()
    {
        var r = Make().Resolve(Scene, 800, 600);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("no_usable_calibration");
    }

    [Fact]
    public void NullScene_ReturnsNone_NoScene()
    {
        var r = Make().Resolve(null, 800, 600);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("no_scene");
    }
}
```

- [ ] **Step 3: Verify the StubCal `IMapCalibrationService` shape matches the live interface**

Run: `grep -n "public.*GetOverlayCalibration\|public.*GetTextureCalibration\|public.*GetCalibration\|public.*IsCalibrated\|public.*Upsert\|public.*Clear\|public.*Changed" src/Mithril.MapCalibration/IMapCalibrationService.cs`
Expected: all members in StubCal match the interface (member set + signatures). If a member is missing from StubCal, add it as a `throw new NotImplementedException()` or empty body — the resolver only calls `GetOverlayCalibration` / `GetTextureCalibration`.

- [ ] **Step 4: Run the test file**

Run: `dotnet test tests/Mithril.Overlay.Tests --filter "FullyQualifiedName~ComposedOverlayCalibrationResolverTests"`
Expected: 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add tests/Mithril.Overlay.Tests/ComposedOverlayCalibrationResolverTests.cs
git commit -m "$(cat <<'EOF'
mithril#1096 — Move composed-cal decision table tests onto the resolver

Preserves all 8 cases from ResolveComposedOverlayCalibrationTests verbatim;
adds MissReason assertion for each None-returning case (the post-#1096 addition
needed by Legolas' LogCalibrationFallback dedup).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `IOverlayWindow.GetSurfaceSize()` + `OverlayWindowService` swap

**Files:**
- Modify: `src/Mithril.Overlay/IOverlayWindow.cs`
- Modify: `src/Mithril.Overlay/Internal/OverlayWindowService.cs`

- [ ] **Step 1: Add `GetSurfaceSize` to the interface**

Edit `src/Mithril.Overlay/IOverlayWindow.cs`. After the `RegisterScene(...)` method declaration (the last member, line ~104), add:

```csharp
    /// <summary>The live D2D overlay surface's DIU size. Returns
    /// <c>(0, 0)</c> when the window or its surface isn't realised yet
    /// (callers treat as the F2 "unsized_surface" fail-soft branch). Mirror
    /// of <see cref="System.Windows.FrameworkElement.ActualWidth"/> /
    /// <see cref="System.Windows.FrameworkElement.ActualHeight"/> on the
    /// underlying surface.
    ///
    /// <para>mithril#1096: exposed so VM consumers (Legolas) can supply the
    /// same surface dims to <see cref="IComposedOverlayCalibrationResolver"/>
    /// that the marker projection block uses internally.</para></summary>
    (double Width, double Height) GetSurfaceSize();
```

- [ ] **Step 2: Implement `GetSurfaceSize` on `OverlayWindowService` + delete the private helper**

Edit `src/Mithril.Overlay/Internal/OverlayWindowService.cs`.

Delete the `ResolveOverlaySurfaceSize` private method (lines ~585–601, the whole `/// <summary>` block and the method body).

In its place, add a `public` override-style implementation. Locate the existing `RegisterScene` method (search for `public IDisposable RegisterScene`) and add the new method directly after it:

```csharp
    /// <inheritdoc />
    public (double Width, double Height) GetSurfaceSize()
    {
        var window = _window;
        if (window is null) return (0, 0);
        var surface = window.OverlaySurface;
        if (surface is null) return (0, 0);
        return (surface.ActualWidth, surface.ActualHeight);
    }
```

- [ ] **Step 3: Replace internal calls to `ResolveOverlaySurfaceSize()` with `GetSurfaceSize()`**

In `OverlayWindowService.cs`, find every call to `ResolveOverlaySurfaceSize()` (there are 2: one in `ResolveComposedOverlayCalibration` line ~581, one in `ClassifyComposedMissReason` line ~616). Both get inlined away in Task 4, but for THIS task just rename them:

```csharp
// before: var (w, h) = ResolveOverlaySurfaceSize();
var (w, h) = GetSurfaceSize();
```

(Two call sites, mechanical replace.)

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Mithril.Overlay/Mithril.Overlay.csproj`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Overlay/IOverlayWindow.cs src/Mithril.Overlay/Internal/OverlayWindowService.cs
git commit -m "$(cat <<'EOF'
mithril#1096 — Lift ResolveOverlaySurfaceSize to public IOverlayWindow.GetSurfaceSize

Same body, behaviour-neutral. Exposed so VM consumers (Legolas) can supply the
same surface dims to IComposedOverlayCalibrationResolver that the marker
projection block uses internally.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `OverlayWindowService` consumes the resolver (delete dead local methods)

**Files:**
- Modify: `src/Mithril.Overlay/Internal/OverlayWindowService.cs`

- [ ] **Step 1: Add `IComposedOverlayCalibrationResolver` to the constructor**

Edit `src/Mithril.Overlay/Internal/OverlayWindowService.cs`. Locate the constructor (around line 114) and:

1. Add a new ctor parameter `IComposedOverlayCalibrationResolver composedResolver` BEFORE the trailing `ILoggerFactory? loggerFactory = null`. (Required, not optional — production wiring is the only path; tests use `DriveSceneForTest` which doesn't run the surface render loop.)
2. Add a field `private readonly IComposedOverlayCalibrationResolver _composedResolver;` near the other readonly fields (line ~84).
3. Assign in the ctor body.

After the edit, the relevant slice should read:

```csharp
private readonly IComposedOverlayCalibrationResolver _composedResolver;  // mithril#1096
// ... other fields ...

public OverlayWindowService(
    WorldOverlayMarkers markers,
    MarkerSceneRenderer renderer,
    IMapCalibrationService calibration,
    IAreaState areaState,
    IMapState mapState,
    ISceneAssetCache sceneCache,
    IDomainEventSubscriber bus,
    IPositionState positionState,
    ILiveMapViewService liveView,
    IMapTextureDimensions textureDimensions,
    IComposedOverlayCalibrationResolver composedResolver,   // mithril#1096
    ILoggerFactory? loggerFactory = null)
{
    // ... existing assignments ...
    _composedResolver = composedResolver;
    _loggerFactory = loggerFactory;
    _logger = loggerFactory?.CreateLogger("Mithril.Overlay");
    _sceneContext = new OverlaySceneContext(this);
}
```

- [ ] **Step 2: Replace `ResolveComposedOverlayCalibration` body with a call to the resolver**

Replace the private `ResolveComposedOverlayCalibration` method (around line 575–583) with:

```csharp
/// <summary>
/// mithril#1081 / #1096 — thin pass-through to the shared
/// <see cref="IComposedOverlayCalibrationResolver"/>. Returns the legacy
/// <c>(Cal, Path)</c> tuple shape so existing call sites at lines 371 + 660
/// stay one-liner-clean.
/// </summary>
private (WorldToOverlayCalibration? Cal, CalPath Path)
    ResolveComposedOverlayCalibration(MapSceneRef? scene)
{
    var (w, h) = GetSurfaceSize();
    var r = _composedResolver.Resolve(scene, w, h);
    return (r.Calibration, r.Path);
}
```

- [ ] **Step 3: Delete `ResolveComposedOverlayCalibrationForTest`**

Delete the entire internal static method (around lines 522–567). The 8-case test now exercises the resolver directly (Task 2).

- [ ] **Step 4: Replace `ClassifyComposedMissReason` body with a call to the resolver**

Replace the `ClassifyComposedMissReason` method (around lines 609–620) with:

```csharp
/// <summary>
/// mithril#1081 / #1096 — re-derives the reason a composed overlay calibration
/// could not be built for a calibrated scene. Now delegates to the resolver's
/// MissReason (called at most once per scene per session, gated by
/// <see cref="_projectionMissAreasLogged"/>).
/// </summary>
private string ClassifyComposedMissReason(MapSceneRef scene)
{
    var (w, h) = GetSurfaceSize();
    var r = _composedResolver.Resolve(scene, w, h);
    return r.MissReason ?? "unknown";
}
```

(The internal "unexpected (overlay-frame cal present)" string the old classifier returned is dropped — the resolver returns `DirectOverlay`/`null` reason in that case, and the call site in `OnSurfaceRender` already gates on `composedCal is null`, so the classifier is only invoked when composition genuinely failed.)

- [ ] **Step 5: Drop the now-unused `using` (if present) for the deleted internal enum scope**

The `CalPath` references in `OverlayWindowService.cs` switch from `internal enum CalPath` (local) to the public `Mithril.Overlay.CalPath`. Same namespace — no using change needed. Delete the local `internal enum CalPath { ... }` block (lines ~507–519).

- [ ] **Step 6: Build**

Run: `dotnet build Mithril.slnx`
Expected: build succeeds. (If a Mithril shell is open the build-block hook fires — close it first per `mithril_build_file_lock_silent` memory.)

- [ ] **Step 7: Run all Mithril.Overlay.Tests**

Run: `dotnet test tests/Mithril.Overlay.Tests`
Expected: all tests pass. The OverlayWindowService construction now needs the resolver — verify any tests that `new OverlayWindowService(...)` directly construct one (search for `new OverlayWindowService` in `tests/Mithril.Overlay.Tests`) and patch the constructor calls. Use a simple lambda or pre-built `ComposedOverlayCalibrationResolver(stubCal, stubDims)` per test.

- [ ] **Step 8: Commit**

```bash
git add src/Mithril.Overlay/Internal/OverlayWindowService.cs tests/Mithril.Overlay.Tests
git commit -m "$(cat <<'EOF'
mithril#1096 — OverlayWindowService consumes IComposedOverlayCalibrationResolver

Deletes the internal CalPath enum, ResolveComposedOverlayCalibrationForTest
pure helper, and ClassifyComposedMissReason — all subsumed by the resolver.
Behaviour-neutral: cal.path tag values and per-scene miss logging unchanged.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `MapOverlayViewModel` infrastructure — ctor params + `ResolveOverlayCal` helper

**Files:**
- Modify: `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs`
- Modify: `src/Legolas.Module/LegolasModule.cs`

- [ ] **Step 1: Extend the long constructor with two optional trailing parameters**

Edit `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs`. Locate the 15-arg public ctor (line 69). Add two optional trailing parameters:

```csharp
public MapOverlayViewModel(
    SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer,
    SurveyFlowController surveyFlow, LegolasBrushes brushes,
    LegolasSettings? settings,
    PinCalibrationCoordinator? pinCalibration = null,
    IPositionState? positionState = null,
    IDomainEventSubscriber? bus = null,
    IAreaCalibrationService? areaCalibration = null,
    MotherlodeMeasurementCoordinator? motherlode = null,
    ICharacterPinAnchor? characterPin = null,
    IWorldOverlayMarkers? markers = null,
    IAreaState? areaState = null,
    Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
    ILiveMapViewService? liveView = null,
    IComposedOverlayCalibrationResolver? composedResolver = null,   // mithril#1096
    IOverlayWindow? overlayWindow = null)                            // mithril#1096
{
    // ... existing assignments ...
    _composedResolver = composedResolver;
    _overlayWindow = overlayWindow;
}
```

Add two readonly fields near `_areaCalibration` (line 33):

```csharp
private readonly IComposedOverlayCalibrationResolver? _composedResolver;   // mithril#1096
private readonly IOverlayWindow? _overlayWindow;                            // mithril#1096
```

Add the using directives at the top of the file:

```csharp
using Mithril.Overlay;
```

(Verify if already present — `IWorldOverlayMarkers` lives in `Mithril.Overlay` already so the using is likely there.)

- [ ] **Step 2: Add the `ResolveOverlayCal` private helper**

Add this private method to `MapOverlayViewModel`, immediately before the existing `LogCalibrationFallback` method (~line 1229):

```csharp
/// <summary>mithril#1096 — single point of policy for "give me a usable
/// overlay-frame calibration for the current scene." When the composer +
/// overlay window are wired (production, new tests), routes through the
/// shared <see cref="IComposedOverlayCalibrationResolver"/> so texture-frame-
/// only records compose onto the live surface (parity with OverlayWindowService).
/// When EITHER is null (legacy test ctors that don't wire them), falls back to
/// the pre-#1096 direct-overlay-only read so every existing test stays green.
/// Returns <c>(Cal, Path, MissReason)</c>; consumers feed MissReason into
/// <see cref="LogCalibrationFallback"/>'s dedup key.</summary>
private (WorldToOverlayCalibration? Cal, CalPath Path, string? MissReason) ResolveOverlayCal()
{
    if (_composedResolver is not null && _overlayWindow is not null)
    {
        var (w, h) = _overlayWindow.GetSurfaceSize();
        var r = _composedResolver.Resolve(_areaCalibration?.CurrentScene, w, h);
        return (r.Calibration, r.Path, r.MissReason);
    }
    // Legacy path: pre-#1096 direct-overlay-only behaviour. Mirrors what every
    // call site did before this migration; preserves the contract for test
    // ctors that don't wire the new dependencies.
    var direct = _areaCalibration?.CurrentOverlayCalibration;
    return direct is not null
        ? (direct, CalPath.DirectOverlay, null)
        : (null, CalPath.None, "no_overlay_cal");
}
```

- [ ] **Step 3: Update the DI factory in `LegolasModule.cs`**

Edit `src/Legolas.Module/LegolasModule.cs` line 158–179. Append the two new parameters to the `new MapOverlayViewModel(...)` factory:

```csharp
services.AddSingleton<MapOverlayViewModel>(sp =>
    new MapOverlayViewModel(
        sp.GetRequiredService<SessionState>(),
        sp.GetRequiredService<ICoordinateProjector>(),
        sp.GetRequiredService<IRouteOptimizer>(),
        sp.GetRequiredService<SurveyFlowController>(),
        sp.GetRequiredService<LegolasBrushes>(),
        sp.GetRequiredService<LegolasSettings>(),
        sp.GetService<PinCalibrationCoordinator>(),
        sp.GetService<IPositionState>(),
        sp.GetService<IDomainEventSubscriber>(),
        sp.GetService<IAreaCalibrationService>(),
        sp.GetService<MotherlodeMeasurementCoordinator>(),
        sp.GetService<ICharacterPinAnchor>(),
        sp.GetService<Mithril.Overlay.IWorldOverlayMarkers>(),
        sp.GetService<IAreaState>(),
        sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>(),
        sp.GetService<Mithril.MapCalibration.ILiveMapViewService>(),
        // mithril#1096 — composed-cal migration:
        sp.GetService<Mithril.Overlay.IComposedOverlayCalibrationResolver>(),
        sp.GetService<Mithril.Overlay.IOverlayWindow>()));
```

- [ ] **Step 4: Build to verify infrastructure compiles**

Run: `dotnet build src/Legolas.Module/Legolas.Module.csproj`
Expected: build succeeds.

- [ ] **Step 5: Run existing Legolas tests to verify legacy fallback preserves behaviour**

Run: `dotnet test tests/Legolas.Tests`
Expected: all existing tests pass. The new ctor parameters are optional → existing test fixtures don't pass them → `_composedResolver is null` → `ResolveOverlayCal()` falls back to legacy direct-overlay read. Zero behavioural change for existing tests.

- [ ] **Step 6: Commit**

```bash
git add src/Legolas.Module/ViewModels/MapOverlayViewModel.cs src/Legolas.Module/LegolasModule.cs
git commit -m "$(cat <<'EOF'
mithril#1096 — MapOverlayViewModel ctor + ResolveOverlayCal helper

Two new optional ctor params (IComposedOverlayCalibrationResolver, IOverlayWindow)
+ a private ResolveOverlayCal helper that routes through the composer when
wired and falls back to direct-overlay-only when not (preserves every existing
test fixture). No call sites switched yet — that's tasks 6–8.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Migrate `RebuildCalibrationGhosts` + integration test (headline behaviour)

**Files:**
- Modify: `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs` (RebuildCalibrationGhosts)
- Create: `tests/Legolas.Tests/ViewModels/MapOverlayComposedCalMigrationTests.cs`

- [ ] **Step 1: Write the headline failing test**

Create `tests/Legolas.Tests/ViewModels/MapOverlayComposedCalMigrationTests.cs`:

```csharp
using Arda.Contracts;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.MapCalibration;
using Mithril.Overlay;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// mithril#1096 — VM consumers route through IComposedOverlayCalibrationResolver
/// when wired. Headline behaviour: a scene with only a texture-frame record
/// (no overlay-frame record) projects ghosts via the composed path.
/// </summary>
public sealed class MapOverlayComposedCalMigrationTests
{
    private const string Sha = "test-sha";

    private static CalibrationReference Ref(string name, double x, double z) =>
        new(name, "Landmark", new WorldCoord(x, 0, z));

    private sealed class StubCal : IMapCalibrationService
    {
        public WorldToOverlayCalibration? OverlayCal { get; set; }
        public WorldToTextureCalibration? TextureCal { get; set; }
        public WorldToOverlayCalibration? GetOverlayCalibration(MapSceneRef scene) => OverlayCal;
        public WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene) => TextureCal;
        public AreaCalibration? GetCalibration(MapSceneRef scene) => null;
        public bool IsCalibrated(MapSceneRef scene) => OverlayCal is not null || TextureCal is not null;
        public event EventHandler<MapSceneRef>? Changed { add { } remove { } }
        public void Upsert(MapSceneRef scene, AreaCalibration calibration) { }
        public void Clear(MapSceneRef scene) { }
    }

    private sealed class StubDims : IMapTextureDimensions
    {
        public (int Width, int Height)? TryGetSizeBySha(string? sha) =>
            string.Equals(sha, Sha, StringComparison.Ordinal) ? (1024, 1024) : null;
    }

    private sealed class StubOverlayWindow : IOverlayWindow
    {
        public (double W, double H) Size { get; set; } = (800, 600);
        public (double Width, double Height) GetSurfaceSize() => Size;
        public System.Windows.Window Window => throw new NotSupportedException();
        public bool IsReady => true;
        public string? StatusMessage => null;
        public void SetStatusMessage(string? message) { }
        public IDisposable RegisterScene(Action<IOverlaySceneContext> draw) =>
            new Reg();
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        private sealed class Reg : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void RebuildCalibrationGhosts_TextureFrameOnly_ComposesAndRenders()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var areaCal = new FakeAreaCalibrationService();
        areaCal.SetReferences(Ref("Statue", 10, 5), Ref("Well", -4, 12));
        // FakeAreaCalibrationService.CurrentOverlayCalibration is direct-only —
        // when we set a calibration it returns non-null. To exercise the
        // texture-frame-only path through the COMPOSER, we DON'T set a
        // calibration on the fake (its CurrentOverlayCalibration stays null),
        // and we wire the StubCal below with ONLY a texture-frame record.
        // The ResolveOverlayCal helper then sees _composedResolver is non-null
        // → calls it → composes → returns the composed cal.

        var stubCal = new StubCal
        {
            TextureCal = new WorldToTextureCalibration(
                OriginX: 50, OriginY: 75, Scale: 2.0,
                RotationRadians: 0, MirrorNorth: false) { PixelSha256 = Sha },
        };
        var composer = new Mithril.Overlay.Internal.ComposedOverlayCalibrationResolver(stubCal, new StubDims());
        var overlayWindow = new StubOverlayWindow();

        // Patch FakeAreaCalibrationService to report a non-null CurrentScene
        // even without a calibration — required so ResolveOverlayCal passes a
        // non-null MapSceneRef to the composer. The fake's stub returns
        // MapSceneRef("AreaTest", null, "Map_AreaTest") only when calibration
        // is non-null today; we use the IsCurrentAreaCalibrated path
        // (calibration set) to make CurrentScene non-null AND simulate the
        // gate the toggle command needs.
        areaCal.SetCalibration(new AreaCalibration(2.0, 0.0, 100, 200, 3, 1.5));
        // But: we want CurrentOverlayCalibration to return null so the
        // direct-path fallback DOESN'T fire. The fake projects calibration
        // → CurrentOverlayCalibration non-null. We can't get composition
        // through this fake. The test instead uses the OVERLAY path: route
        // through the composer's StubCal which has only a texture-frame
        // record. The fake's CurrentOverlayCalibration is non-null but the
        // ResolveOverlayCal helper prefers the COMPOSER's result when wired,
        // so we need the composer to be the source of truth — see the
        // implementation in Task 5 step 2.
        //
        // The ResolveOverlayCal helper as written DOES delegate to the
        // composer whenever (_composedResolver, _overlayWindow) are both
        // non-null, IGNORING the fake's CurrentOverlayCalibration. That's
        // the intended production behaviour: the composer is authoritative.

        var map = new MapOverlayViewModel(
            session, projector, optimizer, surveyFlow, brushes,
            settings, pinCalibration: null, positionState: null, bus: null,
            areaCalibration: areaCal,
            motherlode: null, characterPin: null, markers: null, areaState: null,
            loggerFactory: null, liveView: null,
            composedResolver: composer, overlayWindow: overlayWindow);

        map.IsCurrentAreaCalibrated.Should().BeTrue();
        map.ToggleCalibrationValidationCommand.CanExecute(null).Should().BeTrue();

        map.ToggleCalibrationValidationCommand.Execute(null);

        map.CalibrationGhosts.Should().HaveCount(2,
            "the texture-frame-only record composes onto the overlay surface and projects both refs");
    }
}
```

- [ ] **Step 2: Run the test to verify it FAILS**

Run: `dotnet test tests/Legolas.Tests --filter "FullyQualifiedName~MapOverlayComposedCalMigrationTests"`
Expected: FAIL. The current `RebuildCalibrationGhosts` reads `_areaCalibration?.CurrentOverlayCalibration` directly — the fake returns the direct cal, so ghosts render via the legacy path. The test asserts ghosts render via composition, but won't distinguish the path without the migration.

Actually — re-read the assertion: `HaveCount(2)`. The fake DOES set a direct cal so ghosts render today. So the test passes pre-migration too! That's a bad test.

Tighten the test by REMOVING the direct-cal seeding and verifying ghosts render with ONLY a texture-frame record. Update the fake's `SetCalibration` to a new helper `SetTextureFrameOnly()` — or use a different `IAreaCalibrationService` double for this test.

**Replace the test body with a corrected version:**

```csharp
[Fact]
public void RebuildCalibrationGhosts_TextureFrameOnly_ComposesAndRenders()
{
    var session = new SessionState();
    var settings = new LegolasSettings();
    var surveyFlow = new SurveyFlowController(session, settings);
    var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
    var projector = new CoordinateProjector();
    var brushes = new LegolasBrushes(settings);

    // Texture-frame-only IAreaCalibrationService double: IsCurrentAreaCalibrated
    // is true (the scene IS calibrated, just only in the texture frame), but
    // CurrentOverlayCalibration returns null — exactly the pre-#1096 silent-drop
    // shape.
    var areaCal = new TextureFrameOnlyAreaCalibration(
        Ref("Statue", 10, 5), Ref("Well", -4, 12));

    var stubCal = new StubCal
    {
        TextureCal = new WorldToTextureCalibration(
            OriginX: 50, OriginY: 75, Scale: 2.0,
            RotationRadians: 0, MirrorNorth: false) { PixelSha256 = Sha },
    };
    var composer = new Mithril.Overlay.Internal.ComposedOverlayCalibrationResolver(stubCal, new StubDims());
    var overlayWindow = new StubOverlayWindow();

    var map = new MapOverlayViewModel(
        session, projector, optimizer, surveyFlow, brushes,
        settings, pinCalibration: null, positionState: null, bus: null,
        areaCalibration: areaCal,
        motherlode: null, characterPin: null, markers: null, areaState: null,
        loggerFactory: null, liveView: null,
        composedResolver: composer, overlayWindow: overlayWindow);

    map.IsCurrentAreaCalibrated.Should().BeTrue(
        "the area IS calibrated — texture-frame record exists");
    map.ToggleCalibrationValidationCommand.CanExecute(null).Should().BeTrue();

    map.ToggleCalibrationValidationCommand.Execute(null);

    map.CalibrationGhosts.Should().HaveCount(2,
        "post-#1096: texture-frame-only record composes onto the overlay surface and projects both refs");
}

private sealed class TextureFrameOnlyAreaCalibration : IAreaCalibrationService
{
    private readonly IReadOnlyList<CalibrationReference> _refs;

    public TextureFrameOnlyAreaCalibration(params CalibrationReference[] refs) { _refs = refs; }

    // The headline of #1096: direct-overlay null, but IsCurrentAreaCalibrated true.
    public WorldToOverlayCalibration? CurrentOverlayCalibration => null;
    public bool IsCurrentAreaCalibrated => true;
    public AreaCalibration? CurrentCalibration =>
        // Source field unused by the success-log path (uses ?.Source.ToString()
        // ?? "<unknown>"), but a non-null value is needed for the IsCurrentAreaCalibrated
        // path's downstream code to behave; return a sentinel.
        new AreaCalibration(1.0, 0.0, 0, 0, 0, 0.0);
    public MapSceneRef? CurrentScene =>
        new MapSceneRef("AreaTest", null, "Map_AreaTest");
    public string? CurrentAreaFriendlyName => "Test Area";
    public IReadOnlyList<CalibrationReference> CurrentAreaReferences => _refs;
    public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();

    public event EventHandler? Changed { add { } remove { } }
    public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }

    public void SelectScene(MapSceneRef scene) { }
    public AreaCalibration? CalibrateCurrentArea(
        IReadOnlyList<(WorldCoord World, OverlayPixel Pixel)> placements,
        double calibrationZoom = 1.0) => null;
    public void ClearCurrentAreaCalibration() { }
    public void NoteSurvey(string name, MetreOffset offset) { }
}
```

- [ ] **Step 3: Re-run the test to verify it FAILS pre-migration**

Run: `dotnet test tests/Legolas.Tests --filter "FullyQualifiedName~MapOverlayComposedCalMigrationTests"`
Expected: FAIL. The pre-migration `RebuildCalibrationGhosts` reads `_areaCalibration?.CurrentOverlayCalibration` directly → null → returns empty. Test asserts `HaveCount(2)` → fails.

- [ ] **Step 4: Migrate `RebuildCalibrationGhosts` (line ~629) to use `ResolveOverlayCal`**

Edit `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs`. Replace the body of `RebuildCalibrationGhosts` (line 629 through ~689) with:

```csharp
private void RebuildCalibrationGhosts()
{
    using var act = MithrilActivitySources.LegolasCalibration.StartActivity("calibration.ghosts.rebuild");
    var sw = Stopwatch.StartNew();

    CalibrationGhosts.Clear();
    var (cal, path, missReason) = ResolveOverlayCal();
    if (cal is null)
    {
        var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
        LogCalibrationFallback(skippedArea, "RebuildCalibrationGhosts", missReason ?? "no_overlay_cal");
        MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
            new KeyValuePair<string, object?>("consumer", "ghosts"),
            new KeyValuePair<string, object?>("area", skippedArea));
        act?.SetTag("cal.path", "none");
        act?.SetTag("area", skippedArea);
        return;
    }
    var areaKey = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
    var ghostFix = areaKey != "<unknown>" ? _liveView?.GetCurrent(areaKey) : null;
    var refs = _areaCalibration!.CurrentAreaReferences;
    foreach (var g in GhostLabelDeclutter.Build(refs, cal.Value, ghostFix))
        CalibrationGhosts.Add(g);
    OnPropertyChanged(nameof(CalibrationValidationStatus));

    var source = _areaCalibration.CurrentCalibration?.Source.ToString() ?? "<unknown>";
    var residual = _areaCalibration.CurrentCalibration?.ResidualPixels ?? double.NaN;
    _logger?.LogInformation(
        "RebuildCalibrationGhosts({Area}): built {Ghosts} from {Refs} refs (cal source={Source}, residual={Residual:0.00}px).",
        areaKey, CalibrationGhosts.Count, refs.Count, source, residual);

    act?.SetTag("area", areaKey);
    act?.SetTag("refs_count", refs.Count);
    act?.SetTag("ghosts_built", CalibrationGhosts.Count);
    act?.SetTag("cal.path", path switch
    {
        CalPath.DirectOverlay => "direct_overlay",
        CalPath.ComposedFromTexture => "composed_from_texture",
        _ => "none",
    });
    act?.SetTag("cal.source", source);
    act?.SetTag("cal.residual_px", residual);

    MithrilMeters.LegolasCalibration.GhostsRebuildMs.Record(
        sw.Elapsed.TotalMilliseconds,
        new KeyValuePair<string, object?>("area", areaKey),
        new KeyValuePair<string, object?>("refs_count", refs.Count),
        new KeyValuePair<string, object?>("ghosts_built", CalibrationGhosts.Count));
}
```

- [ ] **Step 5: Run the migration test to verify it PASSES**

Run: `dotnet test tests/Legolas.Tests --filter "FullyQualifiedName~MapOverlayComposedCalMigrationTests"`
Expected: PASS. The composer wires the texture-frame record + stub dims → returns a composed `WorldToOverlayCalibration` → ghosts project.

- [ ] **Step 6: Run all Legolas tests to verify no regressions**

Run: `dotnet test tests/Legolas.Tests`
Expected: all tests pass. Existing tests don't wire `composedResolver` → `ResolveOverlayCal` falls back to `_areaCalibration?.CurrentOverlayCalibration` → unchanged behaviour.

- [ ] **Step 7: Commit**

```bash
git add src/Legolas.Module/ViewModels/MapOverlayViewModel.cs tests/Legolas.Tests/ViewModels/MapOverlayComposedCalMigrationTests.cs
git commit -m "$(cat <<'EOF'
mithril#1096 — Migrate RebuildCalibrationGhosts to ResolveOverlayCal

Headline behaviour now lands: a scene with only a texture-frame record
(AutoCal-produced, no wizard solve) projects pink dots via the composer
when wired. Integration test asserts ghosts.Count == 2 for the previously-
silent texture-frame-only case.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Migrate `MotherlodeMarkerPixels` + `MotherlodeGuidanceOverlay`

**Files:**
- Modify: `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs`

- [ ] **Step 1: Replace `MotherlodeMarkerPixels` cal check (line ~1407)**

In `MotherlodeMarkerPixels` (around line 1401), replace the cal-resolution block:

```csharp
// Before:
if (_areaCalibration?.CurrentOverlayCalibration is not { } cal)
{
    var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
    LogCalibrationFallback(skippedArea, "MotherlodeMarkerPixels", "no_overlay_cal");
    MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
        new KeyValuePair<string, object?>("consumer", "motherlode_markers"),
        new KeyValuePair<string, object?>("area", skippedArea));
    return Array.Empty<OverlayPixel>();
}

// After:
var (cal, _, missReason) = ResolveOverlayCal();
if (cal is null)
{
    var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
    LogCalibrationFallback(skippedArea, "MotherlodeMarkerPixels", missReason ?? "no_overlay_cal");
    MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
        new KeyValuePair<string, object?>("consumer", "motherlode_markers"),
        new KeyValuePair<string, object?>("area", skippedArea));
    return Array.Empty<OverlayPixel>();
}
```

Below the null-check, the existing code uses `cal.Value.ToLiveOverlay(...)`. Since the variable is now `WorldToOverlayCalibration? cal`, after the null-check we have a non-null nullable — adjust to `cal.Value` (already correct in the existing code at line 1439 `cal.ToLiveOverlay`, which needs to become `cal.Value.ToLiveOverlay`).

- [ ] **Step 2: Replace `MotherlodeGuidanceOverlay` cal check (line ~1455)**

In `MotherlodeGuidanceOverlay` getter, apply the same pattern:

```csharp
// Before:
if (_areaCalibration?.CurrentOverlayCalibration is not { } cal)
{
    var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
    LogCalibrationFallback(skippedArea, "MotherlodeGuidanceOverlay", "no_overlay_cal");
    MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
        new KeyValuePair<string, object?>("consumer", "motherlode_guidance"),
        new KeyValuePair<string, object?>("area", skippedArea));
    return null;
}

// After:
var (cal, _, missReason) = ResolveOverlayCal();
if (cal is null)
{
    var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
    LogCalibrationFallback(skippedArea, "MotherlodeGuidanceOverlay", missReason ?? "no_overlay_cal");
    MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
        new KeyValuePair<string, object?>("consumer", "motherlode_guidance"),
        new KeyValuePair<string, object?>("area", skippedArea));
    return null;
}
```

Below the null-check, `cal.ToLiveOverlay(...)` and `cal.Scale` become `cal.Value.ToLiveOverlay(...)` and `cal.Value.Scale`.

- [ ] **Step 3: Build + run all Legolas tests**

Run: `dotnet build src/Legolas.Module/Legolas.Module.csproj && dotnet test tests/Legolas.Tests`
Expected: build succeeds; all tests pass (the per-frame getters' legacy fallback preserves existing behaviour; no new tests required since the headline test in Task 6 already proves the composition path).

- [ ] **Step 4: Commit**

```bash
git add src/Legolas.Module/ViewModels/MapOverlayViewModel.cs
git commit -m "$(cat <<'EOF'
mithril#1096 — Migrate MotherlodeMarkerPixels + MotherlodeGuidanceOverlay

Per-frame getters now resolve cal via ResolveOverlayCal — the texture-frame-only
composed path lights the motherlode marker + guidance ring up in scenes that
previously rendered nothing.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Migrate `RefreshSurveyPlayerAnchor` + `SetCalibrationValidation` toggle log

**Files:**
- Modify: `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs`

- [ ] **Step 1: Replace `RefreshSurveyPlayerAnchor` cal read (line ~320)**

In `RefreshSurveyPlayerAnchor` (line 311), replace:

```csharp
// Before:
var overlayCal = _areaCalibration?.CurrentOverlayCalibration;
if (overlayCal is null && _latestTrackerFix is not null)
{
    var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
    LogCalibrationFallback(skippedArea, "RefreshSurveyPlayerAnchor", "no_overlay_cal");
    MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
        new KeyValuePair<string, object?>("consumer", "survey_anchor"),
        new KeyValuePair<string, object?>("area", skippedArea));
}

// After:
var (overlayCal, _, missReason) = ResolveOverlayCal();
if (overlayCal is null && _latestTrackerFix is not null)
{
    var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
    LogCalibrationFallback(skippedArea, "RefreshSurveyPlayerAnchor", missReason ?? "no_overlay_cal");
    MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
        new KeyValuePair<string, object?>("consumer", "survey_anchor"),
        new KeyValuePair<string, object?>("area", skippedArea));
}
```

`overlayCal` flows into `ResolveSurveyAnchor(...)` unchanged downstream.

- [ ] **Step 2: Replace `SetCalibrationValidation` toggle-log read (line ~590) — rename to `overlayCalUsable`**

In `SetCalibrationValidation` (line 580), replace:

```csharp
// Before:
var overlayCalPresent = _areaCalibration?.CurrentOverlayCalibration is not null;
// ... (action / branch logic unchanged) ...
_logger?.LogInformation(
    "SetCalibrationValidation(on={On}, area={Area}, scene={Scene}, isCalibrated={IsCalibrated}, overlayCalPresent={OverlayCalPresent}): {Action} → ghostsBuilt={GhostsBuilt}.",
    on, area, scene?.SceneFriendlyName ?? "<none>", isCalibrated, overlayCalPresent, action, CalibrationGhosts.Count);

// After:
var overlayCalUsable = ResolveOverlayCal().Cal is not null;
// ... (action / branch logic unchanged) ...
_logger?.LogInformation(
    "SetCalibrationValidation(on={On}, area={Area}, scene={Scene}, isCalibrated={IsCalibrated}, overlayCalUsable={OverlayCalUsable}): {Action} → ghostsBuilt={GhostsBuilt}.",
    on, area, scene?.SceneFriendlyName ?? "<none>", isCalibrated, overlayCalUsable, action, CalibrationGhosts.Count);
```

(Property name rename is intentional — post-migration the value means "present-OR-composable," not "present.")

- [ ] **Step 3: Search for any test that asserts on `overlayCalPresent` literal**

Run: `grep -rn "overlayCalPresent\|OverlayCalPresent" tests/`
Expected: zero hits. If any test does assert on the literal, update it to `overlayCalUsable` / `OverlayCalUsable`.

- [ ] **Step 4: Build + run all Legolas tests**

Run: `dotnet build src/Legolas.Module/Legolas.Module.csproj && dotnet test tests/Legolas.Tests`
Expected: build succeeds; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Legolas.Module/ViewModels/MapOverlayViewModel.cs
git commit -m "$(cat <<'EOF'
mithril#1096 — Migrate RefreshSurveyPlayerAnchor + SetCalibrationValidation toggle log

Survey "you-are-here" anchor projects through the composed path in texture-
frame-only scenes. SetCalibrationValidation toggle log renames overlayCalPresent
→ overlayCalUsable so the lifecycle anchor's semantic shift (present-OR-
composable) is explicit for triagers.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Migrate `PlayerLogIngestionService.HandleMapTarget`

**Files:**
- Modify: `src/Legolas.Module/Services/PlayerLogIngestionService.cs`
- Modify: `src/Legolas.Module/LegolasModule.cs`

- [ ] **Step 1: Add ctor parameters + fields**

Edit `src/Legolas.Module/Services/PlayerLogIngestionService.cs`. Add two optional trailing parameters to the constructor (line 71):

```csharp
public PlayerLogIngestionService(
    IDomainEventSubscriber bus,
    IAreaCalibrationService areaCalibration,
    SurveyFlowController flow,
    SessionState session,
    MotherlodeMeasurementCoordinator motherlode,
    LegolasSettings settings,
    ILoggerFactory? loggerFactory = null,
    ILiveMapViewService? liveView = null,
    IComposedOverlayCalibrationResolver? composedResolver = null,   // mithril#1096
    IOverlayWindow? overlayWindow = null)                            // mithril#1096
{
    // ... existing assignments ...
    _composedResolver = composedResolver;
    _overlayWindow = overlayWindow;
    _logger = loggerFactory?.CreateLogger("Legolas.Ingestion");
}
```

Add fields near `_areaCalibration` (line 53):

```csharp
private readonly IComposedOverlayCalibrationResolver? _composedResolver;   // mithril#1096
private readonly IOverlayWindow? _overlayWindow;                            // mithril#1096
```

Add the using:

```csharp
using Mithril.Overlay;
```

- [ ] **Step 2: Add a sibling `ResolveOverlayCal()` helper at the service**

Mirror the helper from `MapOverlayViewModel`. Add this private method near the bottom of the file:

```csharp
/// <summary>mithril#1096 — same pattern as MapOverlayViewModel.ResolveOverlayCal:
/// route through the composer when wired; fall back to direct-overlay-only
/// behaviour when not (preserves existing tests).</summary>
private (WorldToOverlayCalibration? Cal, string? MissReason) ResolveOverlayCal()
{
    if (_composedResolver is not null && _overlayWindow is not null)
    {
        var (w, h) = _overlayWindow.GetSurfaceSize();
        var r = _composedResolver.Resolve(_areaCalibration.CurrentScene, w, h);
        return (r.Calibration, r.MissReason);
    }
    var direct = _areaCalibration.CurrentOverlayCalibration;
    return direct is not null ? (direct, null) : (null, "no_overlay_cal");
}
```

- [ ] **Step 3: Migrate the `HandleMapTarget` cal check (line ~210)**

Replace:

```csharp
// Before:
if (_areaCalibration.CurrentOverlayCalibration is not { } cal)
{
    _session.LastLogEvent =
        $"Map target: {cleanName} @ ({world.X:0},{world.Z:0}) → area not calibrated; run pin calibration";
    var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
    _logger?.LogInformation(
        "HandleMapTarget {Name}@({X:0},{Z:0}) area={Area}: dropped — area not calibrated.",
        cleanName, world.X, world.Z, skippedArea);
    MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
        new KeyValuePair<string, object?>("consumer", "survey_pin"),
        new KeyValuePair<string, object?>("area", skippedArea));
    return;
}

// After:
var (calNullable, missReason) = ResolveOverlayCal();
if (calNullable is not { } cal)
{
    _session.LastLogEvent =
        $"Map target: {cleanName} @ ({world.X:0},{world.Z:0}) → area not calibrated; run pin calibration";
    var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
    _logger?.LogInformation(
        "HandleMapTarget {Name}@({X:0},{Z:0}) area={Area} reason={Reason}: dropped — area not calibrated.",
        cleanName, world.X, world.Z, skippedArea, missReason ?? "no_overlay_cal");
    MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
        new KeyValuePair<string, object?>("consumer", "survey_pin"),
        new KeyValuePair<string, object?>("area", skippedArea));
    return;
}
```

(`cal` is reused downstream verbatim — no further code changes in the method.)

- [ ] **Step 4: Update the DI factory in `LegolasModule.cs`**

`PlayerLogIngestionService` is registered via `services.AddHostedService<PlayerLogIngestionService>()` at line 272 — that registration uses .NET's default activator, which auto-resolves all ctor params from the container. Both new params are optional, so the activator will pass null when not registered.

**But** the activator passes positional args; the trailing `composedResolver` / `overlayWindow` params are optional with `= null` defaults. The activator skips optional params it can't resolve. To make absolutely sure the container DOES resolve them (when registered), no factory change is needed — `AddHostedService<T>` uses `ActivatorUtilities.CreateInstance` which inspects every ctor param and resolves what it can from the container.

Verify by reading the resulting boot.log: the `Subscribed to Arda domain events` log line should fire on shell start (per #1093 D10), and a Trace at the new resolver-class category would be visible if any composition happens. No code change required here — but smoke-test in Task 11.

- [ ] **Step 5: Build + run all Legolas tests**

Run: `dotnet build src/Legolas.Module/Legolas.Module.csproj && dotnet test tests/Legolas.Tests`
Expected: build succeeds; all tests pass. Existing `PlayerLogIngestionService` tests don't wire the new params; legacy path preserves behaviour.

- [ ] **Step 6: Commit**

```bash
git add src/Legolas.Module/Services/PlayerLogIngestionService.cs
git commit -m "$(cat <<'EOF'
mithril#1096 — Migrate PlayerLogIngestionService.HandleMapTarget

Survey-pin drop now resolves cal via the composer when wired — texture-frame-
only scenes accept absolute map-pin drops in Survey mode instead of silently
falling through "area not calibrated."

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Migrate `CalibrationSessionViewModel.ProjectLandmarks` (wizard)

**Files:**
- Modify: `src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs`
- Modify: `src/Legolas.Module/LegolasModule.cs`

- [ ] **Step 1: Add ctor parameter + field**

Edit `src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs`. Add an optional trailing param to the ctor (line 35):

```csharp
public CalibrationSessionViewModel(
    IAreaCalibrationService service,
    IDomainEventSubscriber? bus = null,
    Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
    IComposedOverlayCalibrationResolver? composedResolver = null)   // mithril#1096
{
    _service = service;
    _service.Changed += OnServiceChanged;
    _service.SurveyObserved += OnSurveyObserved;
    _pinSub = bus?.Subscribe<MapPinAdded>(OnPinAdded);
    _logger = loggerFactory?.CreateLogger("Legolas.CalibrationSession");
    _composedResolver = composedResolver;
    Refresh();
}
```

Add field next to `_service` (line 31):

```csharp
private readonly IComposedOverlayCalibrationResolver? _composedResolver;   // mithril#1096
```

Add the using:

```csharp
using Mithril.Overlay;
```

- [ ] **Step 2: Migrate `ProjectLandmarks` (line ~535)**

Replace:

```csharp
// Before:
[RelayCommand]
private void ProjectLandmarks()
{
    GhostPins.Clear();
    if (_service.CurrentOverlayCalibration is not { } c)
    {
        ClickWarning = "Solve a calibration first — nothing to project.";
        var skippedArea = _service.CurrentScene?.MapAssetKey ?? "<unknown>";
        _logger?.LogInformation(
            "ProjectLandmarks: refused — no overlay calibration; UI surfaced 'Solve a calibration first'.");
        MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
            new KeyValuePair<string, object?>("consumer", "wizard_landmarks"),
            new KeyValuePair<string, object?>("area", skippedArea));
        RaiseDebug();
        return;
    }
    var skipped = 0;
    foreach (var r in References)
    {
        if (Placements.Any(p => ReferenceEquals(p.Reference, r))) { skipped++; continue; }
        GhostPins.Add(new GhostPin(r.Name, c.ToOverlay(r.World)));
    }
    // ... (success log unchanged) ...
}

// After:
[RelayCommand]
private void ProjectLandmarks()
{
    GhostPins.Clear();

    // mithril#1096: resolve via the composer when wired (passes the wizard
    // canvas dims from _viewportW/_viewportH, populated by Viewport_SizeChanged
    // in CalibrationOverlayView). Fall back to direct-overlay-only on legacy
    // ctor paths so existing tests stay green.
    WorldToOverlayCalibration? c;
    string? missReason;
    if (_composedResolver is not null)
    {
        var r = _composedResolver.Resolve(_service.CurrentScene, _viewportW, _viewportH);
        c = r.Calibration;
        missReason = r.MissReason;
    }
    else
    {
        c = _service.CurrentOverlayCalibration;
        missReason = c is null ? "no_overlay_cal" : null;
    }

    if (c is null)
    {
        ClickWarning = "Solve a calibration first — nothing to project.";
        var skippedArea = _service.CurrentScene?.MapAssetKey ?? "<unknown>";
        _logger?.LogInformation(
            "ProjectLandmarks: refused — no overlay calibration (reason={Reason}); UI surfaced 'Solve a calibration first'.",
            missReason ?? "no_overlay_cal");
        MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
            new KeyValuePair<string, object?>("consumer", "wizard_landmarks"),
            new KeyValuePair<string, object?>("area", skippedArea));
        RaiseDebug();
        return;
    }
    var skipped = 0;
    foreach (var r in References)
    {
        if (Placements.Any(p => ReferenceEquals(p.Reference, r))) { skipped++; continue; }
        GhostPins.Add(new GhostPin(r.Name, c.Value.ToOverlay(r.World)));
    }
    // ... (success log unchanged) ...
}
```

(`c` is now nullable `WorldToOverlayCalibration?` — adjust the `c.ToOverlay(r.World)` call to `c.Value.ToOverlay(r.World)`.)

- [ ] **Step 3: Update the DI factory in `LegolasModule.cs`**

Edit `src/Legolas.Module/LegolasModule.cs` lines 183–187:

```csharp
services.AddSingleton<CalibrationSessionViewModel>(sp =>
    new CalibrationSessionViewModel(
        sp.GetRequiredService<IAreaCalibrationService>(),
        sp.GetService<IDomainEventSubscriber>(),
        sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>(),
        sp.GetService<Mithril.Overlay.IComposedOverlayCalibrationResolver>()));   // mithril#1096
```

- [ ] **Step 4: Build + run all Legolas tests**

Run: `dotnet build src/Legolas.Module/Legolas.Module.csproj && dotnet test tests/Legolas.Tests`
Expected: build succeeds; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs src/Legolas.Module/LegolasModule.cs
git commit -m "$(cat <<'EOF'
mithril#1096 — Migrate CalibrationSessionViewModel.ProjectLandmarks

Wizard ghost landmarks project through the composer when wired, using the
existing _viewportW/_viewportH already populated by CalibrationOverlayView's
Viewport_SizeChanged hook. Texture-frame-only scenes can now ghost landmarks
in the wizard preview.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Tag descriptor doc + perf-trace schema docs

**Files:**
- Modify: `src/Legolas.Module/Diagnostics/LegolasCalibrationTagDescriptors.cs:45`
- Modify: `docs/perf-trace-schema.md`

- [ ] **Step 1: Update the `cal.path` tag descriptor doc-comment**

Edit `src/Legolas.Module/Diagnostics/LegolasCalibrationTagDescriptors.cs`. Change the line:

```csharp
// Before:
new("cal.path",              PiiClassification.Safe, Subsystem, "Projection path taken: direct_overlay | none (the composed-cal migration adds composed)."),

// After:
new("cal.path",              PiiClassification.Safe, Subsystem, "Projection path taken: direct_overlay | composed_from_texture | none. mithril#1096 finalised the vocabulary; the resolver returns ComposedFromTexture when only a texture-frame record exists and composes onto the live surface."),
```

- [ ] **Step 2: Update `docs/perf-trace-schema.md`**

Locate the `cal.path` row in `docs/perf-trace-schema.md` (search for `cal.path`). Update the values column to `direct_overlay | composed_from_texture | none`. Add a paragraph below the table:

```markdown
**`cal.path = composed_from_texture` (mithril#1096):** the producer resolved
the overlay-frame calibration by composing a texture-frame record onto the
target surface (overlay window or wizard canvas) via
`WorldToTextureCalibration.ProjectThroughOverlay(MapRect)` with dims from
`IMapTextureDimensions`. Indicates the post-#1081 AutoCal record path lit up
on this scene.

**`ProjectionSkipped` counter semantic note (mithril#1096):** the counter
no longer increments when a texture-frame record successfully composes —
the scene hit the happy path via the composer. Only `Path == None`
outcomes increment the counter. A drop in this counter for a given
`area` between pre- and post-#1096 builds is the migration landing
correctly, not a regression.

**MissReason vocabulary on `LogCalibrationFallback` Trace records:** when
the composer returns `Path == None`, the consumer feeds the resolver's
`MissReason` into the dedup helper:

| MissReason | Cause |
|---|---|
| `no_scene` | `CurrentScene` is null (no `MapAssetChanged` yet). |
| `no_usable_calibration` | Picker returned neither overlay nor texture record. |
| `null_sha` | Texture record exists but `PixelSha256` is null (pre-#1081). User re-runs AutoCalibrate. |
| `unsized_surface` | Surface dims ≤ 0 (window not realised; wizard not laid out). |
| `catalogue_miss` | Texture sha doesn't match `CanonicalAssetHashes`. |
| `no_overlay_cal` | Legacy fallback (composer not wired — should not appear in production builds). |
```

- [ ] **Step 3: Commit**

```bash
git add src/Legolas.Module/Diagnostics/LegolasCalibrationTagDescriptors.cs docs/perf-trace-schema.md
git commit -m "$(cat <<'EOF'
mithril#1096 — Document composed_from_texture cal.path value + MissReason vocab

Finalises the cal.path tag value vocabulary in the descriptor doc-comment
and adds the perf-trace schema entry plus the MissReason cheatsheet for
LogCalibrationFallback Trace records.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Manual headline verify + INDEX status flip

**Files:**
- Modify: `docs/planning/INDEX.md`

- [ ] **Step 1: Build the full solution**

Run: `dotnet build Mithril.slnx`
Expected: build succeeds. (Close any open Mithril shell first per `mithril_build_file_lock_silent` memory.)

- [ ] **Step 2: Run the FULL test suite**

Run: `dotnet test Mithril.slnx`
Expected: every test passes.

- [ ] **Step 3: Launch the shell against a known AutoCal-only scene**

Run: `pwsh -File scripts/start.ps1` (or invoke the `mithril` skill).

Manually:
1. Pick an area with only an AutoCal-produced texture-frame record (no wizard solve). Suggested: the scene that triggered the #1093 investigation. If unsure, run AutoCalibrate from scratch on a fresh area to land a texture-frame-only record.
2. Open the Map tab.
3. Toggle calibration validation (the wizard's Validate button or hotkey).
4. **Verify pink dots render on the overlay window**, anchored to the area's known landmarks.
5. Switch to Survey mode; drop a `@me` map pin in-game. Confirm the survey pin renders on the overlay.
6. Switch to Motherlode mode (with at least one solved treasure visible); confirm the marker dot + guidance ring render.

If any of (4)–(6) fails, the migration didn't reach a downstream gate — a coordinator-level swap defeated by a renderer-level check is exactly the failure mode `verify_headline_behavior_through_full_render_chain` memory documents. DO NOT mark complete — open a follow-up issue and stop.

- [ ] **Step 4: Capture a perf-trace JSONL during the verify**

Enable perf-recording from the diagnostics UI (Settings → Diagnostics → Record perf-trace), repeat the toggle from step 3, stop recording, find the resulting `mithril-*.json` file (typically `%LocalAppData%/Mithril/perf-traces/`).

Confirm:

```bash
grep -c "composed_from_texture" /path/to/mithril-trace.json
```

Expected: at least 1 hit on `cal.path = composed_from_texture` on the spans `calibration.ghosts.rebuild` or `project`.

- [ ] **Step 5: Flip the INDEX row status to `shipped`**

Edit `docs/planning/INDEX.md`. Locate the `calibration-1096-vm-composed-cal-migration` row and change `active` → `shipped`. Add the PR number alongside the issue link (use whatever PR number `gh pr view` reports after the next step's push).

- [ ] **Step 6: Push, open the PR, attach evidence**

Push the branch:

```bash
git push -u origin HEAD
```

Open PR:

```bash
gh pr create --title "mithril#1096 — Migrate VM projection paths to composed-cal" --body "$(cat <<'EOF'
## Summary

- New `IComposedOverlayCalibrationResolver` in `Mithril.Overlay` lifts the composition logic out of `OverlayWindowService` so VM consumers share one resolver.
- 7 VM-side reads of `_areaCalibration.CurrentOverlayCalibration` switch to a `ResolveOverlayCal` helper that routes through the composer when wired, falls back to direct-overlay-only when not (preserves every existing test).
- `IOverlayWindow.GetSurfaceSize()` lifts the surface-dim accessor to a public contract method.
- New `cal.path = composed_from_texture` value documented in tag descriptors + `docs/perf-trace-schema.md`.
- Headline behaviour: texture-frame-only AutoCal records now render pink dots / motherlode markers / survey pins / wizard ghost landmarks.

## Test plan

- [x] All existing `dotnet test Mithril.slnx` passes (legacy fallback path).
- [x] New `MapOverlayComposedCalMigrationTests` asserts ghosts render via composition on a texture-frame-only `IAreaCalibrationService` stub.
- [x] Renamed `ComposedOverlayCalibrationResolverTests` covers the 8-case decision table + new `MissReason` assertions.
- [x] Manual verify on a real AutoCal-only scene: pink dots, survey pin, motherlode marker all render. Perf-trace JSONL shows `cal.path = composed_from_texture` (see attached evidence).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Attach a screenshot of the rendered pink dots + a perf-trace JSONL excerpt showing `cal.path = composed_from_texture` to the PR description.

- [ ] **Step 7: Commit the INDEX flip**

```bash
git add docs/planning/INDEX.md
git commit -m "$(cat <<'EOF'
docs: flip calibration-1096 planning INDEX row to shipped

Headline behaviour verified live: texture-frame-only scenes render pink dots
via the composed path. PR linked in the row.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
git push
```

---

## Self-Review

**1. Spec coverage:**

| Spec section | Tasks |
|---|---|
| §2 in-scope sites (×7) | Tasks 6 (ghosts), 7 (motherlode markers + guidance), 8 (survey anchor + toggle), 9 (ingestion), 10 (wizard) |
| §4.1 new types (CalPath public, ComposedCalResolution, IComposedOverlayCalibrationResolver, impl) | Tasks 0, 1 |
| §4.2 `IOverlayWindow.GetSurfaceSize()` | Task 3 |
| §4.3 wizard reads existing `_viewportW`/`_viewportH` (no new infra) | Task 10 step 2 |
| §5 per-site migration | Tasks 6–10 |
| §6.1 `cal.path = composed_from_texture` descriptor + doc | Task 11 |
| §6.2 `ProjectionSkipped` semantic change | Task 7 + 8 (implicit: counter only fires on `cal is null`); doc note in Task 11 |
| §6.3 `MissReason` vocabulary | Task 1 step 1 (resolver impl); Task 11 docs |
| §7.1 composer unit tests | Task 2 |
| §7.2 integration test on `RebuildCalibrationGhosts` | Task 6 |
| §7.3 manual headline verify | Task 12 |
| §8 phasing (one PR, ordered) | Tasks 0 → 12 sequence |
| §10 verification owed | Task 4 step 6 (existing tests pass after composer swap), Task 10 (verify viewport fields are the right space), Task 12 (live verify) |

All spec requirements covered.

**2. Placeholder scan:** No "TBD", "TODO", "fill in", "add appropriate error handling". Every step includes the exact code or command.

**3. Type consistency:**
- `CalPath` enum values: `None`, `DirectOverlay`, `ComposedFromTexture` — consistent everywhere.
- `ComposedCalResolution` record fields: `Calibration`, `Path`, `MissReason` — consistent.
- `ResolveOverlayCal()` helper signature on `MapOverlayViewModel` returns `(Cal, Path, MissReason)`; sibling on `PlayerLogIngestionService` returns `(Cal, MissReason)` (no Path needed there since no span tag). Documented difference.
- MissReason vocabulary: `no_scene`, `no_usable_calibration`, `null_sha`, `unsized_surface`, `catalogue_miss`, plus legacy-path `no_overlay_cal`. Consistent across resolver impl (Task 1), tests (Task 2), and docs (Task 11).

No inconsistencies.
