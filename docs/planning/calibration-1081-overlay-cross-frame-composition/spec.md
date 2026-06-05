# Legolas overlay cross-frame composition (mithril#1081)

**Status:** active
**Issue:** [#1081](https://github.com/moumantai-gg/mithril/issues/1081)
**Prerequisites:** [#1077](https://github.com/moumantai-gg/mithril/pull/1077) (pixel-frame typing + `ProjectThroughOverlay` bridge) shipped; [#1082](https://github.com/moumantai-gg/mithril/issues/1082) / [#1085](https://github.com/moumantai-gg/mithril/pull/1085) (frame-aware `UserRefinementStore`, per-frame typed slots) shipped.
**Blocks:** [#914](https://github.com/moumantai-gg/mithril/issues/914) — AutoCalibration GA release. Last remaining AutoCal release blocker per [#1077 spec §12](../calibration-1076-pixel-frame-typing/spec.md).
**Supersedes:** the v1 draft of this spec (dims stamped on cal record). The v2 design below content-addresses the texture identity and looks up dimensions in the existing canonical-asset-hash catalogue.

## 1 — Goal & non-goals

**Goal.** Make the Legolas overlay render markers correctly for any scene whose only calibration is AutoCalibration-produced (texture-frame). The composition primitive [`WorldToTextureCalibration.ProjectThroughOverlay(MapRect)`](../../../src/Mithril.MapCalibration/WorldToTextureCalibration.cs) was wired into the type system by #1077 §6.2; this issue consumes it at the two overlay render call sites, and supplies the missing inputs (the base texture's native dimensions) by content-addressing the canonical-asset-hash catalogue with a sha stamped on the calibration record.

**Design posture.** Authoritative source of texture dimensions is the **per-asset pixel content** — same digest the sidecar's [`MapTextureManifest`](../../../src/Mithril.MapCalibration.Detection/Internal/MapTextureManifest.cs) carries and the [`CanonicalAssetHashGate`](../../../src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashGate.cs) checks at AutoCal-load time. Bundled data is **convenience** (head-start for cold-start renders + bundled-baseline scenes); the canonical catalogue is **authority** (what dimensions correspond to what sha). When PG ships a patch that changes a texture, Mithril ships a catalogue refresh — same release cadence as the hash gate today.

**Non-goals.**
- **Gwaihir's pannable/zoomable map ([#914](https://github.com/moumantai-gg/mithril/issues/914) follow-up).** Mithril's overlay is strictly 1:1 with the in-game map ([XAML invariant](../../../src/Mithril.Overlay/Internal/OverlayWindow.xaml), legolas-overview §Pitfalls). Gwaihir's pan/zoom path computes a non-trivial `MapRect` from its own viewport, not from the overlay surface's fill size. Out of scope; tracked under #914.
- **New end-user UX.** No new chip, no new setting, no new hotkey.
- **Retroactive renderability for pre-#1081 records.** Texture-frame records persisted before #1081 lands (developer-only — AutoCal has never shipped) load with `PixelSha256 = null` and remain unrenderable on the overlay until next AutoCal solve re-stamps. Drift-check is unaffected (doesn't need dims).
- **Caching the composed `WorldToOverlayCalibration` across frames.** Compose math is six multiplies + four adds per frame; cache lifecycle is more code than the math saves.
- **Per-Mithril-version catalogue refresh tooling automation.** The catalogue is hand-curated at #1081 commit time + at each PG-patch follow-up release. A dev-time harvest tool falls out of the same workflow that emits sidecar manifests; building it can be a follow-up if the manual workflow proves painful.

## 2 — Problem statement

After #1077 typed the calibration → overlay pipeline, [`IMapCalibrationService.WorldToOverlay`](../../../src/Mithril.MapCalibration/IMapCalibrationService.cs#L54) returns null when no overlay-frame record exists for the scene. Two overlay sites read this:

- **Marker projection.** [`OverlayWindowService.ProjectMarkers`](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs#L459) drops the marker on null.
- **Scene-drawer `Project`.** [`OverlaySceneContext.Project`](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs#L711) skips the pin on null. Survey wedges, Motherlode pin layers, route polylines — every world-coord scene-drawer goes through here.

For a scene whose only calibration is AutoCalibration-produced (texture-frame), the texture-frame record exists but `WorldToOverlay` returns null. `IsCalibrated(scene)` is true (the chip says "calibrated") but the overlay renders nothing. Silent end-to-end miss.

**Why this doesn't bite today.** AutoCalibration has never shipped in a tagged release. Every in-the-wild calibration record is Legolas-wizard-produced (overlay-frame). The class is a sleeping bug; AutoCal GA wakes it for every end user who accumulates a texture-frame record on a scene they haven't run the wizard for.

**Why the existing primitive doesn't fix this on its own.** [`WorldToTextureCalibration.ProjectThroughOverlay(MapRect overlayRect)`](../../../src/Mithril.MapCalibration/WorldToTextureCalibration.cs) returns a usable `WorldToOverlayCalibration`, but the overlay rect needs the texture's native dimensions. The overlay surface knows its own size (`ActualWidth/Height`); the texture's dimensions are not currently surfaced at any seam the overlay touches.

## 3 — Design

### 3.1 Architecture summary

```
AutoCal solve  ──stamps──▶  AreaCalibration { …, PixelSha256 }
                            persisted in refinements.json (additive nullable string)

CanonicalAssetHashes inner-dict value extends (Schema v1→v2):
   byPgVersion[pg][areaKey]:  string  →  { sha, width, height }
   ↑ hash-gate consumers read .sha; new dim consumers read .width/.height
   ↑ catalogue resource already shipped (today as empty stub) at
     src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json

Per render frame in OverlayWindowService.OnSurfaceRender:
  1. existing scene-resolution + IsCalibrated  (unchanged)
  2. NEW: resolve composed WorldToOverlayCalibration? for this scene
        a) try GetOverlayCalibration(scene)              → wizard-fit case (direct)
        b) if null: try GetTextureCalibration(scene), then:
             → dims = _textureDimensions.TryGetSizeBySha(texCal.PixelSha256)
             → null sha or null dims → composed = null (cal.path = none)
             → otherwise build MapRect(0, 0, surface.W, surface.H, dims.W, dims.H)
                          → texCal.ProjectThroughOverlay(rect)
        c) if both null: composed = null
  3. bind via BeginFrame → marker loop + Project() both read bound cal
```

Net surface: cal grows **one nullable string** (texture identity). Catalogue's existing two-level dict grows from `string → string` (sha-only) to `string → { sha, width, height }` (sha + dims). Render path adds a tiny `IMapTextureDimensions` lookup in core.

### 3.2 Cal record changes — sha-only

Additive on [`AreaCalibration`](../../../src/Mithril.MapCalibration/AreaCalibration.cs):

```csharp
/// <summary>
/// SHA-256 (lowercase hex) of the base texture this calibration was solved
/// against — same digest the sidecar's MapTextureManifest carries and the
/// CanonicalAssetHashGate checks. Stamped at AutoCal-solve time (mithril#1081)
/// and on bundled-baseline rows at commit time. Identifies WHICH texture the
/// math is bound to; the overlay derives the texture's pixel dimensions by
/// looking this up via IMapTextureDimensions (backed by the same catalogue
/// the hash gate consumes). Null on records persisted before #1081 →
/// unrenderable on the overlay (drift-check unaffected — it doesn't need
/// dims, and is gated on a separate frame check).
/// </summary>
public string? PixelSha256 { get; init; }
```

Mirror on [`WorldToTextureCalibration`](../../../src/Mithril.MapCalibration/WorldToTextureCalibration.cs) (the typed picker carries it through `MapCalibrationService.ToTextureCalibration`). Overlay-frame records leave it null — they don't compose against a texture.

**Stamping at AutoCal solve.** [`AutoCalibrationEngine.cs:717-721`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L717) grows one assignment. `baseTexture` (a `GrayImage`) is in scope at line 520; SHA-256 over `baseTexture.Pixels` (~1 MB at 1024² → sub-millisecond):

```csharp
var stamped = result.Calibration with
{
    Source = CalibrationSource.AutoCapture,
    Frame = CalibrationFrame.Texture,
    PixelSha256 = Convert.ToHexStringLower(SHA256.HashData(baseTexture.Pixels)),
};
```

No interface change on [`IBaseTextureProvider`](../../../src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs). The hash is re-derived from gray pixels we already hold. (The provider's underlying [`CachedBaseTextureProvider`](../../../src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs) already hashes at load time for integrity — same value; we just recompute rather than thread it through a new signature.)

**Bundled-baseline rows.** Each anchor in [`map-calibration-baseline.json`](../../../src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json) adds a `pixelSha256` field at #1081 commit time. Values come from the same harvest that populates the catalogue (§3.4).

### 3.3 Catalogue extension — Schema v1→v2

[`CanonicalAssetHashes`](../../../src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashes.cs) inner-dict value changes shape from `string` (sha-only) to a record carrying sha + dims:

```csharp
// Lifted to core (Mithril.MapCalibration) — see §3.5 for the assembly decision.
public sealed record CanonicalAssetHashEntry(
    string Sha,
    int    Width,
    int    Height);

public sealed record CanonicalAssetHashes(
    int SchemaVersion,
    Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>> ByPgVersion);
```

Schema v1 → v2. [`canonical-asset-hashes.json`](../../../src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json) example after refresh:

```json
{
  "schemaVersion": 2,
  "byPgVersion": {
    "467": {
      "AreaSerbule":     { "sha": "abc…def", "width": 1024, "height": 1024 },
      "AreaEltibule":    { "sha": "012…789", "width": 1024, "height": 1024 },
      "AreaKurMountains":{ "sha": "456…abc", "width": 1024, "height": 1024 }
    }
  }
}
```

**Inner-key format.** Today the hash gate is called from [`CachedBaseTextureProvider.cs:94`](../../../src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs#L94) with `mapAssetKey` (e.g. `"Map_AreaSerbule"`), so the inner key IS the full Unity Texture2D name. The example above shows bare area keys; the actual format is determined by the existing call site — verify and document at PR-1 time. **Verification owed.**

**Schema v1 → v2 load fallback.** Existing v1 records have the inner-dict value as a bare string. The loader detects v1 (`SchemaVersion == 1` or `null`) and treats every entry as `{ Sha: <string>, Width: 0, Height: 0 }`. Hash-gate consumers continue to read `.Sha` unchanged. Dim consumers see `0/0` → catalogue miss → render skip. Today's empty stub catalogue ships v1; the v2 bump lands populated.

**Loader.** [`CanonicalAssetHashGate.ReadCatalogue`](../../../src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashGate.cs#L101) gains v1-detection + entry-rewriting. Internal change; no consumer-facing signature impact.

### 3.4 Catalogue population at #1081 commit time

The catalogue's `ByPgVersion[pg]` entries are sourced from sidecar manifests at the current PG version. Workflow at commit time:

1. Dev runs the asset-extractor sidecar against a known-good PG install: every supported `Map_<X>` texture is harvested via [`MapTextureCacheEmitter`](../../../tools/Mithril.MapCalibration.Tools.Common/MapTextureCacheEmitter.cs), producing `map-texture-<X>.{json,bin}` files. The manifest's `(pixelSha256, width, height)` are the catalogue's source values.
2. Dev runs a small new harvester (or one-off script) that reads each manifest file under `%LocalAppData%/Mithril/assets/` and emits `byPgVersion[<pg>][<key>] = { sha, width, height }`.
3. The emitted JSON replaces the current empty stub `canonical-asset-hashes.json`.
4. Dev stamps `pixelSha256` on each row of `map-calibration-baseline.json` from the same harvest data (the bundled-baseline asset keys are a subset of the catalogue keys).

**Coverage minimum.** At minimum the catalogue covers (a) every bundled-baseline asset key and (b) every scene AutoCal might solve against. Pragmatically that's "every scene whose `Map_<X>` is reachable in PG" — the asset extractor's full sweep. The catalogue is small (a few kilobytes per PG version) and the manifest harvest is mechanical, so over-covering is cheap.

**Refresh cadence.** When PG patches and textures change, Mithril ships a catalogue refresh (re-run §3.4 against the new PG install). Same as the canonical-hash gate's existing release pattern.

**Coverage gaps post-refresh.** Per-existing hash-gate semantics: PG version absent from catalogue → accept-with-warn (don't hard-fail a newer patch). For dims, "absent → no dims" naturally collapses to "no render this frame, wait for next Mithril release." The hash-gate's warning surface (`HashVerdict.WithWarning`) is the canonical place to signal this state — already wired.

### 3.5 New `IMapTextureDimensions` service in core

Lift the catalogue types from [`Mithril.MapCalibration.Detection.Internal`](../../../src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashes.cs) to `Mithril.MapCalibration` (core), make public. Detection's gate references the core type. Rationale: `Mithril.Overlay` only depends on core today (verified — see csproj); adding a Detection dependency to wire the dim lookup would invert the existing assembly DAG (Capture/Detection sit *above* core; Overlay sits *adjacent* to core).

```csharp
// Mithril.MapCalibration core, new public surface:
public interface IMapTextureDimensions
{
    /// <summary>Look up the canonical (width, height) for a texture by its
    /// SHA-256 (lowercase hex) — the same digest the sidecar's
    /// MapTextureManifest carries and the calibration record stamps. Returns
    /// null when the sha isn't in the catalogue (uncatalogued PG version /
    /// asset, or a stale calibration whose texture has since been replaced).
    /// </summary>
    (int Width, int Height)? TryGetSizeBySha(string? pixelSha256);
}
```

Implementation (also core):

```csharp
internal sealed class CatalogueMapTextureDimensions : IMapTextureDimensions
{
    // Loads canonical-asset-hashes.json from the embedded resource (same
    // resource the gate reads) and pre-builds a sha→(w,h) index across all
    // PG versions in the catalogue. Constant-time lookup at render path.
    private readonly IReadOnlyDictionary<string, (int W, int H)> _bySha;

    public CatalogueMapTextureDimensions(CanonicalAssetHashes catalogue) =>
        _bySha = BuildShaIndex(catalogue);

    public (int Width, int Height)? TryGetSizeBySha(string? pixelSha256)
    {
        if (string.IsNullOrWhiteSpace(pixelSha256)) return null;
        return _bySha.TryGetValue(pixelSha256!, out var dims)
            ? (dims.W, dims.H) : null;
    }

    private static IReadOnlyDictionary<string, (int W, int H)> BuildShaIndex(
        CanonicalAssetHashes catalogue)
    {
        var idx = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var byArtifact in catalogue.ByPgVersion.Values)
            foreach (var entry in byArtifact.Values)
                if (entry.Width > 0 && entry.Height > 0)
                    idx[entry.Sha] = (entry.Width, entry.Height);  // last-writer-wins
        return idx;
    }
}
```

The `bySha` index is content-addressed and PG-version-agnostic — the **same** sha across PG versions has the same dimensions by definition (it IS the same texture content). Collisions across versions are harmless; if the same sha appears under two PG versions with different dims, that's a bundling bug and tests catch it (§6).

DI registration in core's `MapCalibrationServiceCollectionExtensions`: singleton `IMapTextureDimensions` backed by the shared catalogue load.

### 3.6 Render-time wiring

The decision-table at `OverlayWindowService.ResolveComposedOverlayCalibration` (unchanged in shape from the v1 spec; updated only in how the texture path resolves dims):

```csharp
private (WorldToOverlayCalibration? Cal, CalPath Path)
    ResolveComposedOverlayCalibration(MapSceneRef? scene)
{
    if (scene is not { } s) return (null, CalPath.None);

    if (_calibration.GetOverlayCalibration(s) is { } overlayCal)
        return (overlayCal, CalPath.DirectOverlay);

    if (_calibration.GetTextureCalibration(s) is not { } texCal)
        return (null, CalPath.None);

    // Dims come from the catalogue, content-addressed by the texture's sha.
    // Null sha (pre-#1081 cal) → null dims → skip; catalogue miss (uncatalogued
    // asset / PG patch newer than catalogue) → null dims → skip.
    if (_textureDimensions.TryGetSizeBySha(texCal.PixelSha256) is not { } dims)
        return (null, CalPath.None);

    var (w, h) = ResolveOverlaySurfaceSize();
    if (w <= 0 || h <= 0) return (null, CalPath.None);

    var rect = new MapRect(
        OriginX: 0, OriginY: 0,
        Width: (int)w, Height: (int)h,
        TextureWidth: dims.Width, TextureHeight: dims.Height);
    return (texCal.ProjectThroughOverlay(rect), CalPath.ComposedFromTexture);
}
```

`OverlaySceneContext.BeginFrame` grows the `composedCal` parameter; `Project` reads the bound cal; `ProjectMarkers` reshapes to take `WorldToOverlayCalibration?`. **Mechanically identical to the v1 spec's §3.4** — the dims-resolution change is the only delta. Existing test-seam shape stays the same.

### 3.7 Picker tiebreak when both frames exist

Unchanged from v1 spec §3.5. Per #1082's per-frame slots, a scene with both overlay-frame + texture-frame records → `GetOverlayCalibration` returns the overlay-frame record → texture-frame composition is dead code on that scene. Verified by the decision-table fact `BothFramesPresent_PrefersDirectOverlay`.

## 4 — Persistence

### 4.1 `AreaCalibration.PixelSha256` is purely additive

STJ's source-generated context round-trips new `init`-only `string?` properties automatically; pre-#1081 records deserialise the absent field as null. The renderer's "null sha → catalogue miss → skip" guard makes them unrenderable on the overlay until next AutoCal solve re-stamps. Drift-check unaffected. No file-schema bump on `UserRefinementFile` (currently at v3 post-#1082).

### 4.2 `CanonicalAssetHashes` Schema v1 → v2 IS a shape change

The inner-dict value goes from `string` to `CanonicalAssetHashEntry`. JSON shape:

| v1 (today) | v2 (post-#1081) |
|---|---|
| `byPgVersion[pg][key] = "sha…"` | `byPgVersion[pg][key] = { "sha": "…", "width": …, "height": … }` |

Loader detects v1 by `schemaVersion` and wraps each string into `{ Sha: <string>, Width: 0, Height: 0 }` — backwards-compatible read, forward-compatible write. Internal-only consumers; no public API impact.

A v1 record loaded by a post-#1081 build runs the wrapping; dim consumers see 0/0 → no render. A v2 record loaded by a pre-#1081 build would fail (no `CanonicalAssetHashEntry` deserialiser); since the catalogue lives in the same assembly as the loader and ships together, this combination doesn't occur in the wild.

### 4.3 Bundled-baseline writers

[`BundledBaselineLoader`](../../../src/Mithril.MapCalibration/Internal/BundledBaselineLoader.cs) round-trips `PixelSha256` automatically (additive JSON property → STJ handles it). The bundled JSON file gets the field hand-stamped per row at #1081 commit time. The loader's existing `cal with { Source = …, Frame = … }` post-processing leaves `PixelSha256` intact.

`UserRefinementStore` round-trips `PixelSha256` via the source-generated `MapCalibrationJsonContext` — same mechanism that picked up the `Frame` field in #1077.

`AreaCalibrationService` (Legolas wizard writes) leaves `PixelSha256 = null` for overlay-frame records. Wizard-fit cals are solved in overlay-pixel space; they don't compose against a texture.

## 5 — Telemetry

The existing `MithrilActivitySources.Overlay.StartActivity("project")` span at [line 408](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs#L408) grows one tag:

```csharp
renderAct?.SetTag("cal.path", calPath switch
{
    CalPath.DirectOverlay => "direct_overlay",
    CalPath.ComposedFromTexture => "composed_from_texture",
    _ => "none",
});
```

Three values, observable in perf-recorder JSONL. `none` covers all skip cases (uncalibrated, null-sha cal, catalogue miss, unsized surface); the once-per-scene Trace log distinguishes the reason for the user. No new instrument, no new meter. `MithrilMeters.Overlay.ProjectionMisses` continues to count per-scene skips (once per OnSurfaceRender, not per-marker).

## 6 — Test strategy

**Per-PR baseline.** All existing tests stay green. No PR ships with `[Skip]`.

**Unit: cal-record JSON round-trip** (new `tests/Mithril.MapCalibration.Tests/AreaCalibrationTextureShaTests.cs`).
- Write a record with `PixelSha256 = "abc…"`, read it back, assert round-trip.
- Load a handwritten record omitting `pixelSha256`, assert it deserialises with `PixelSha256 = null`.

**Unit: catalogue Schema v1→v2 load fallback** (extend [`tests/Mithril.MapCalibration.Tests/Detection/CanonicalAssetHashGateTests.cs`](../../../tests/Mithril.MapCalibration.Tests/Detection/CanonicalAssetHashGateTests.cs)).
- Load a v1 JSON fixture (bare-string values); assert hash-gate consumers see the wrapped entries with `Sha = <string>`; assert dim consumers see `(0, 0)` → `TryGetSizeBySha` returns null.
- Load a v2 JSON fixture; assert both consumer surfaces work.

**Unit: `MapTextureDimensions` sha index** (new `tests/Mithril.MapCalibration.Tests/MapTextureDimensionsTests.cs`).
- Catalogue with one PG version, three entries → `TryGetSizeBySha(sha)` returns each entry's dims.
- Catalogue with same sha under two PG versions, different dims → bundling-bug case; test asserts the build catches this with a SECOND test fixture that fails to load (see "Bundled catalogue lint" below).
- Empty catalogue → all lookups null.
- Null / empty sha → null.

**Unit: bundled catalogue lint** (new `tests/Mithril.MapCalibration.Tests/BundledCatalogueLintTests.cs`).
- Load the SHIPPED `canonical-asset-hashes.json`; assert no sha collisions with conflicting dims across PG versions (a same-sha-different-dims pair is a bundling bug — dev re-harvested wrong).
- Assert every bundled-baseline row in `map-calibration-baseline.json` carries a `pixelSha256` whose value resolves to an entry in the catalogue with positive dims.

**Unit: `ResolveComposedOverlayCalibration` decision-table** (new `tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs`).
Same shape as v1 spec §6, updated rows:

| Scene state | `GetOverlayCalibration` | `GetTextureCalibration` (`PixelSha256`) | Catalogue knows sha? | Surface size | Expected `cal.path` |
|---|---|---|---|---|---|
| Wizard-only | non-null | null | n/a | any | `direct_overlay` |
| AutoCal-only, sha in catalogue | null | non-null, sha known | yes | > 0 | `composed_from_texture` |
| AutoCal-only, null sha (pre-#1081) | null | non-null, sha null | n/a | > 0 | `none` |
| AutoCal-only, sha NOT in catalogue (newer PG / uncatalogued) | null | non-null, sha known | no | > 0 | `none` |
| AutoCal-only, unsized surface (F2) | null | non-null | yes | 0 | `none` |
| Both frames present | non-null | non-null | yes | > 0 | `direct_overlay` |
| Uncalibrated | null | null | n/a | any | `none` |
| Null scene | n/a | n/a | n/a | any | `none` |

Each row exercised via a pure helper `ResolveComposedOverlayCalibrationForTest(scene, overlayCal, textureCal, dims, surfaceWidth, surfaceHeight)` that takes the four inputs directly so tests don't stand up the full service graph. (Same shape as v1 spec, with `dims` replacing the embedded `textureCal.TextureWidth/Height`.)

**Integration: composed-from-texture through `DriveSceneForTest`** (extend [`tests/Mithril.Overlay.Tests/OverlaySceneHookTests.cs`](../../../tests/Mithril.Overlay.Tests/OverlaySceneHookTests.cs)).
- Fake calibration service returns texture-frame cal with stamped sha; fake `IMapTextureDimensions` returns known dims for that sha; scene drawer's `ctx.Project(x, z)` returns a non-null composed projection.
- Same setup with the catalogue fake returning null for that sha; scene drawer's `Project` returns null.

**AutoCal stamping test** (extend the existing engine tests under `tests/Mithril.MapCalibration.Capture.Tests/`).
- Drive `RunAttemptAsync` end-to-end; assert the persisted `AreaCalibration.PixelSha256` matches `SHA256.HashData(baseTexture.Pixels).ToLowerHex()`.

**Behavioural — manual.** Per [§10](#10--verification-owed).

## 7 — Failure modes

### F1 — Cal has `PixelSha256 = null`

Cause: a record persisted before #1081 lands. Developer-only.

Behaviour: `ResolveComposedOverlayCalibration` skips on the null-sha guard before consulting the catalogue. `cal.path` = `none`, `ProjectionMisses` increments once per scene. Recovery: re-run AutoCalibrate.

### F2 — Cal sha NOT in catalogue

Cause: user is running a PG patch the bundled catalogue hasn't been refreshed against; or, the bundled catalogue is incomplete (a scene's manifest wasn't harvested at commit time).

Behaviour: same as F1 — catalogue miss → null dims → skip. Recovery: next Mithril release ships the refreshed catalogue.

### F3 — Overlay surface not yet laid out

Cause: first render frame after `Show()`; `ActualWidth/Height == 0` until WPF runs layout.

Behaviour: catalogue lookup succeeds (sha is in catalogue) but the surface-size guard returns null. Resolves on the next frame after layout completes.

### F4 — Both frames exist; texture-frame stale relative to overlay-frame

Cause: a scene where the user calibrated via the Legolas wizard (overlay-frame), then later ran AutoCal (texture-frame). #1082 stores both in their typed slots.

Behaviour: `GetOverlayCalibration` returns the wizard record; composition is dead code. The cal's `PixelSha256` is irrelevant on this path.

### F5 — Catalogue sha mismatch (cal stamped against texture X; catalogue now lists sha Y for that asset)

This is the **invalidation case** the v2 design exists to address. The cal's stamped sha is not the catalogue's current sha for the asset, so the `TryGetSizeBySha(cal.PixelSha)` lookup returns null (no entry for the old sha; the catalogue lists the new sha). Falls through to the same skip path as F2.

Recovery: re-run AutoCalibrate on the affected scene. The new solve runs against the live texture; `baseTexture.Pixels` produces the current sha; the stamp is the new sha; the catalogue's current entry covers it → render works.

The user-visible signal is "calibrated chip stays on, overlay is empty" — same shape as F1/F2. A discoverability follow-up (chip text like "texture updated — re-run AutoCalibrate") is out of scope for #1081 but tracked as a follow-up.

## 8 — Risk surface

**Catalogue resource shape change.** `CanonicalAssetHashes.ByPgVersion` value type changes (string → record). Lift the type to core; update Detection to reference. JSON shape change is gated by `schemaVersion` (v1 wraps, v2 reads native). Cross-assembly type move + JSON contract change in one PR is real but bounded.

**Catalogue population at commit time.** The catalogue MUST cover every bundled-baseline asset key + the scenes AutoCal is realistically used against. Coverage is enforced by the lint test (every bundled-baseline `pixelSha256` resolves in the catalogue with positive dims). Coverage of "AutoCal scenes" is harder to assert mechanically; the practical floor is "every Map_<X> in the harvester's sweep."

**SHA-256 cost at AutoCal solve.** ~1 MB at 1024² maps to sub-ms hashing on modern hardware. AutoCal solves take seconds; this is noise. The `CachedBaseTextureProvider` already hashes at load time for integrity, so the cost was already paid once per session — the AutoCal-time re-hash is duplicative but cheap.

**Lift `CanonicalAssetHashes` to core changes accessibility.** Today the record is internal in Detection; bringing it to public-in-core is a small surface-area expansion. Required for `Mithril.Overlay` to consume without crossing into Detection. The alternative (Overlay depends on Detection) inverts the existing assembly DAG.

**Pre-#1064 `Source = AutoCapture` records restamped as `UserRefinement`.** Persistent risk from #1077 §11 — these records (dev environments only) load with `PixelSha256 = null` and fall through to F1. Same recovery: re-run AutoCalibrate.

**Multi-PG-version sha collisions.** Same sha under different PG versions with conflicting dims is a bundling bug (the underlying pixels are by definition the same; if dims differ, the harvest emitted wrong data). The bundled-catalogue lint test catches this at build time.

## 9 — Migration phasing

One PR. Net impact roughly:

| Area | Files | Change |
|---|---|---|
| Core | `AreaCalibration.cs`, `WorldToTextureCalibration.cs` | Add `PixelSha256` init-only field |
| Core | `MapCalibrationService.cs` (`ToTextureCalibration` helper) | Thread `PixelSha256` through to struct |
| Core (lift) | New public `CanonicalAssetHashEntry` + lifted `CanonicalAssetHashes` from Detection | Public surface |
| Core (new) | `IMapTextureDimensions.cs` + `CatalogueMapTextureDimensions.cs` + DI wiring | Tiny new service |
| Core (bundled) | `BundledData/canonical-asset-hashes.json`, `BundledData/map-calibration-baseline.json` | Populate catalogue at current PG version; hand-stamp `pixelSha256` per baseline row |
| Detection | `CanonicalAssetHashGate.cs`, `CanonicalAssetHashes.cs`, `DetectionJsonContext.cs` | Update references to lifted core types; loader handles v1→v2 |
| Capture | `AutoCalibrationEngine.cs` (~line 717) | Stamp `PixelSha256 = SHA256(baseTexture.Pixels)` |
| Overlay | `OverlayWindowService.cs`, `OverlaySceneContext.BeginFrame` | `ResolveComposedOverlayCalibration` consults `IMapTextureDimensions`; `BeginFrame` carries composed cal; `ProjectMarkers` reshape (unchanged from v1 spec mechanics) |
| Tests | `tests/Mithril.MapCalibration.Tests/`, `tests/Mithril.Overlay.Tests/` | Per §6 |
| Docs | `docs/perf-trace-schema.md` | Document `cal.path` tag |

Squash-merged. ~350 LOC net + catalogue JSON population. Larger than the v1 design (which was ~250 LOC + per-row dim stamping); the marginal cost buys content-addressed invalidation + the existing hash gate's by-PG-version curation pattern.

## 10 — Verification owed

- [ ] **Hash-gate inner-key format.** Verify whether [`CachedBaseTextureProvider.cs:94`](../../../src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs#L94) passes `Map_<X>` or bare `<X>` as the `artifactKey` argument; document the format choice in the catalogue's `byPgVersion[pg]` inner-dict and align the dim-lookup code to it. PR-1 prerequisite.
- [ ] **Bundled-baseline `pixelSha256` coverage.** Unit test asserts every bundled row's `pixelSha256` resolves in the catalogue with positive dims. Test fails build if a row is added without a matching catalogue entry.
- [ ] **Picker tiebreak on a scene with both frames.** Decision-table fact covers; manually verify on a real install via the `cal.path` span tag in perf-recorder JSONL.
- [ ] **End-to-end manual verification before AutoCal GA ships.**
  - On a clean profile, trigger AutoCalibration on a scene never wizard-calibrated. Confirm Legolas overlay renders markers via texture-composition. `cal.path` = `composed_from_texture`.
  - Add a Legolas-wizard refinement. Confirm the picker prefers overlay-frame; `cal.path` = `direct_overlay`.
  - Hand-edit `refinements.json` to corrupt a texture-frame entry's `pixelSha256`. Restart. Confirm the overlay skips markers; `cal.path` = `none`; `ProjectionMisses` counter increments.
  - Hand-edit `canonical-asset-hashes.json` to remove an entry that a persisted AutoCal cal references. Restart. Confirm overlay skips markers for that scene; `cal.path` = `none`.

---

*Drafted by Claude (Opus 4.7) during the 2026-06-05 brainstorming session, posted by @arthur-conde. v2 (this revision) supersedes the v1 design (dims-on-cal) — see commit history for v1.*
