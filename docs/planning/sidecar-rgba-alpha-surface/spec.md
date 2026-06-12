# Asset sidecar: surface texture alpha channel to Mithril.MapCalibration — spec

**Issue:** to be filed; sub-issue of [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116). **Status:** brainstorm 2026-06-12, spec landing now. **Consumer:** [`map-calibration-deviation-mask-1116`](../map-calibration-deviation-mask-1116/spec.md) — blocks on this work landing first.

## 1. Problem

The out-of-process asset-extractor sidecar shipped via [mithril#931](https://github.com/moumantai-gg/mithril/issues/931) ([PR #932](https://github.com/moumantai-gg/mithril/pull/932)) decodes PG's map textures and writes a per-area gray-pixel cache that Mithril reads at runtime via [`IBaseTextureProvider`](../../../src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs) → [`CachedBaseTextureProvider`](../../../src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs). The cache format is documented as **"gray-only (no alpha channel)"** ([CachedBaseTextureProvider.cs:16-17](../../../src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs#L16-L17)).

The consumer spec [`map-calibration-deviation-mask-1116`](../map-calibration-deviation-mask-1116/spec.md) needs the texture's **alpha channel** to compute a floor-boundary mask (per its §5.2): opaque pixels = floor, transparent = not-floor. The alpha discarded at decode is exactly the signal needed.

This spec extends the sidecar contract so the alpha channel survives the decode → cache → provider path.

## 2. Goal / scope

**In scope** — surface texture alpha through the existing sidecar + cache + provider stack, mirroring the existing gray-pixel path's discipline (schema-versioned manifest + DeflateStream-compressed binary + SHA-256 integrity + optional canonical-hash gate).

| Change | Goal |
|---|---|
| Sidecar `--maps` decode path emits a second cache file per area | `map-texture-<area>-alpha.{json,bin}` mirrors existing `map-texture-<area>.{json,bin}` shape. Manifest carries width/height/pixelSha256 of the alpha bytes. |
| `CanonicalAssetHashes` catalogue ships **a second entry per area** keyed `<area>-alpha` | Reuses existing `CanonicalAssetHashEntry(Sha, Width, Height)` shape — no entry schema change. Existing gate's `Check(pgVersion, "<area>-alpha", actualHash)` call works as-is. The catalogue's `Dictionary<string, CanonicalAssetHashEntry>` per PG version already supports arbitrary artifact keys (e.g. `"icons"`, area keys) — alpha becomes another key family. |
| `IBaseTextureProvider` gains `TryGetTextureAlpha(mapAssetKey)` method | Returns `GrayImage?` with the alpha as 0/255 grayscale, same width × height as the gray texture for the same key. Same `null` on miss / hash-mismatch / canonical-gate-reject semantics. |
| `CachedBaseTextureProvider` implements the new method | Mirror of existing `TryGetBaseTexture` body: manifest parse → blob decompress → SHA-256 verify → existing `CanonicalAssetHashGate.Check` with `"<area>-alpha"` artifact key → `GrayImage` return. No `CheckAlpha` method addition. |
| `IBaseTextureProvider.TryGetTextureAlpha` returns `null` when the alpha cache is absent | Backwards compat with v1 sidecar caches (only gray texture). Consumer (mask cache) safe-degrades to fog mask only. |
| Unit + integration tests for the round-trip | Sidecar test (cross-repo) writes an RGBA fixture, runs decode, verifies the alpha blob round-trips. Mithril test verifies provider read + fail-soft when alpha absent. |

**Out of scope:**

- The consumer mask computation lives in [`map-calibration-deviation-mask-1116`](../map-calibration-deviation-mask-1116/spec.md), not here.
- Surfacing **full RGBA** (R / G / B / A per pixel) to Mithril. We're surfacing **only the alpha channel as a separate single-channel cache**. The gray + alpha cache files together let the consumer reconstruct the relevant bits without ever introducing an RGBA seam in `IBaseTextureProvider`.
- Changes to icon-template or other sidecar surfaces.
- The sidecar's repo itself is at [`moumantai-gg/mithril-sidecar`](https://github.com/moumantai-gg/mithril-sidecar) (or wherever #931 placed it — verify during impl). This spec describes the Mithril-side consumer contract; the sidecar PR ships in parallel referencing this spec.

## 3. Decision ledger

| # | Decision | Reasoning |
|---|---|---|
| D1 | **Alpha is a separate cache file, not a combined RGBA stream.** | Backwards compatibility — v1 caches (only `map-texture-<area>.{json,bin}`) keep working. The alpha file is purely additive. |
| D2 | **`TryGetTextureAlpha` is a new method on `IBaseTextureProvider`, NOT a new interface.** | Alpha is intrinsically tied to the texture; pairing in the same interface signals the relationship. Avoids a parallel `IBaseTextureAlphaProvider` registration the consumer would have to look up separately. |
| D3 | **`GrayImage?` return type matches existing `TryGetBaseTexture`.** | Caller doesn't have to handle a different image shape. Alpha is naturally single-channel; representing it as a grayscale Mat is appropriate. |
| D4 | **SHA-256 + canonical-hash-gate parity.** | Existing gray-pixel path is hash-verified; alpha must be too. Otherwise a corrupted alpha would silently produce a bad floor-boundary mask the canonical-hash gate wouldn't catch. |
| D5 | **`null` on miss matches existing semantics.** | The `IBaseTextureProvider.cs` XML doc explicitly commits to fail-soft via `null`-on-miss. The new method follows suit. Consumer (mask cache) handles null gracefully (§5.2 of the consumer spec). |
| D6 | **Sidecar emits alpha at the decode step, not on-demand.** | Symmetric with existing gray-pixel emit; one decode pass produces both artifacts. On-demand would add a runtime sidecar call per area-change, which is more complexity and worse perf. |
| D7 | **Canonical-hash catalogue is the source of truth for "this alpha is the expected alpha for this PG version."** | Mirrors how gray-pixel hashes work today ([`CanonicalAssetHashes`](../../../src/Mithril.MapCalibration/CanonicalAssetHashes.cs) keyed by PG version + artifact key → `CanonicalAssetHashEntry(Sha, Width, Height)`). Alpha gets a parallel entry under artifact key `"<area>-alpha"`. **No entry-schema change**; the existing `Dictionary<string, CanonicalAssetHashEntry>` per PG version already accepts arbitrary artifact keys (e.g. `"icons"` coexists with `"Map_AreaSerbule"` today — [`CanonicalAssetHashGate.cs:62-64`](../../../src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashGate.cs#L62-L64) calls `artifactKey` "icons or an area key"). Alpha is just another artifact key family. |

## 4. Architecture overview

```
PG asset bundle (Unity .bundle)
        │
        ▼
┌──────────────────────────────────────────────────┐
│  Sidecar exe (--maps)                            │
│   - decode Texture2D (RGBA)                      │
│   - emit gray pixels →                           │
│       map-texture-<area>.{json,bin}    (existing)│
│   - NEW: emit alpha pixels →                     │
│       map-texture-<area>-alpha.{json,bin}        │
└──────────────────────────────────────────────────┘
        │
        ▼ (filesystem cache at %LocalAppData%/Mithril/assets/)
        │
        ▼
┌──────────────────────────────────────────────────┐
│  CachedBaseTextureProvider (Mithril.MapCalibration) │
│   - TryGetBaseTexture(area)  → GrayImage? (existing) │
│   - TryGetTextureAlpha(area) → GrayImage? (NEW)      │
│      manifest parse → SHA-256 verify → canonical gate│
└──────────────────────────────────────────────────┘
        │
        ▼
   Consumers (FloorBoundaryMaskCache, …)
```

**No on-disk format changes to the existing `map-texture-<area>.{json,bin}` files.** The new alpha is a separate file with the same schema-versioning + integrity pattern.

## 5. Layer-by-layer detail

### 5.1 Sidecar decode + emit (sidecar repo)

The sidecar's `--maps` flow today decodes a Unity Texture2D → grayscale → DeflateStream-compressed blob + manifest. The change adds a parallel emit:

```
decoded RGBA pixels:
  emit gray  → map-texture-<area>.{json,bin}     (existing)
  emit alpha → map-texture-<area>-alpha.{json,bin}  (NEW)
```

**Alpha extraction:** take channel A from the decoded RGBA quad. Range 0-255. Write as 8-bit single-channel binary, same width × height as the gray companion.

**Alpha manifest:**

```json
{
  "schemaVersion": 1,
  "width": 1024,
  "height": 1024,
  "pixelSha256": "<lowercase hex>"
}
```

**Sidecar test:** load an RGBA fixture PNG, run decode, verify both `<area>.bin` and `<area>-alpha.bin` exist, both have matching widths/heights, alpha bytes match expected.

### 5.2 `IBaseTextureProvider` interface change

`src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs`:

```csharp
public interface IBaseTextureProvider
{
    GrayImage? TryGetBaseTexture(string mapAssetKey);

    /// <summary>
    /// The texture's alpha channel for <paramref name="mapAssetKey"/> as a
    /// single-channel <see cref="GrayImage"/>: 0 = transparent, 255 = opaque.
    /// Same width × height as <see cref="TryGetBaseTexture"/> for the same key.
    /// </summary>
    /// <returns><see langword="null"/> when the sidecar didn't emit alpha for
    /// this area, the manifest/blob is missing, integrity check fails, or the
    /// canonical-hash gate rejects.</returns>
    GrayImage? TryGetTextureAlpha(string mapAssetKey);
}
```

### 5.3 `CachedBaseTextureProvider` implementation

`src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs`:

```csharp
public GrayImage? TryGetTextureAlpha(string mapAssetKey)
{
    if (string.IsNullOrWhiteSpace(mapAssetKey)) return null;
    if (string.IsNullOrWhiteSpace(_cacheDir) || !Directory.Exists(_cacheDir))
    {
        _logger?.LogInformation(
            "Base-texture cache dir {CacheDir} absent — no alpha for {MapAsset} (safe-degrade).",
            _cacheDir, mapAssetKey);
        return null;
    }

    var manifestPath = Path.Combine(_cacheDir, $"map-texture-{mapAssetKey}-alpha.json");
    var blobPath     = Path.Combine(_cacheDir, $"map-texture-{mapAssetKey}-alpha.bin");

    var manifest = ReadManifest(manifestPath, mapAssetKey);
    if (manifest is null) return null;

    var pixels = ReadDecompressedPixels(blobPath, mapAssetKey);
    if (pixels is null) return null;

    var actualHash = Convert.ToHexStringLower(SHA256.HashData(pixels));
    if (!string.Equals(actualHash, manifest.PixelSha256, StringComparison.OrdinalIgnoreCase))
    {
        _logger?.LogWarning(
            "Alpha pixel hash mismatch for {MapAsset} (manifest {Expected}, blob {Actual}) — alpha rejected.",
            mapAssetKey, manifest.PixelSha256, actualHash);
        return null;
    }

    int count = manifest.Width * manifest.Height;
    if (count <= 0 || pixels.Length != count) { /* …log + null… */ }

    if (_hashGate is not null)
    {
        // Use the existing Check method with the "<area>-alpha" artifact key
        // convention — no API addition needed; the catalogue holds parallel
        // entries for "Map_X" (gray) and "Map_X-alpha" (alpha).
        var verdict = _hashGate.Check(_pgVersion, $"{mapAssetKey}-alpha", manifest.PixelSha256);
        if (!verdict.Accepted)
        {
            _logger?.LogWarning(
                "Alpha for {MapAsset} rejected by canonical-hash gate: {Reason}.",
                mapAssetKey, verdict.Reason);
            return null;
        }
    }

    _logger?.LogInformation(
        "Loaded alpha for {MapAsset} ({W}x{H}) from {CacheDir} (pixelSha256 verified).",
        mapAssetKey, manifest.Width, manifest.Height, _cacheDir);
    return new GrayImage(manifest.Width, manifest.Height, pixels);
}
```

`ReadManifest` and `ReadDecompressedPixels` are the existing private helpers — same implementations work for the alpha path.

### 5.4 `CanonicalAssetHashes` catalogue extension (NO schema change)

The catalogue's existing entry shape ([`CanonicalAssetHashEntry`](../../../src/Mithril.MapCalibration/CanonicalAssetHashEntry.cs)) is `(Sha, Width, Height)`. The catalogue ships a parallel entry per area under artifact key `"<area>-alpha"`. Both gray and alpha share the same entry shape — alpha's `Width` + `Height` should match gray's (the alpha is the same pixel grid).

```json
{
  "schemaVersion": 2,
  "perPgVersion": {
    "3.0.0.82+1c3350381f": {
      "Map_HogansKeepBasement":       { "sha": "<gray>",  "width": 1024, "height": 1024 },
      "Map_HogansKeepBasement-alpha": { "sha": "<alpha>", "width": 1024, "height": 1024 },   // NEW
      "icons":                         { /* unchanged */ },
      …
    }
  }
}
```

**No catalogue schema bump** — `schemaVersion` stays at 2 because the entry shape didn't change. Only new keys appear. v2 readers without alpha entries still validate gray normally; alpha lookups just hit the existing accept-with-warn "artifact absent" branch ([`CanonicalAssetHashGate.cs:83-89`](../../../src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashGate.cs#L83-L89)) → soft rollout.

### 5.6 Telemetry

The existing sidecar load span (from #931) gains tags for the alpha path:

- `texture.alpha.available` (bool) — alpha cache present + verified
- `texture.alpha.rejected` (enum: `empty_key` / `cache_dir_absent` / `manifest_missing` / `blob_missing` / `hash_mismatch` / `size_mismatch` / `canonical_gate_reject`). The `manifest_missing` and `blob_missing` values cover both the "file not found" and "parse failed / decompress failed" cases — the granularity differentiation lives in the log message body, not the tag. `docs/perf-trace-schema.md` is the live contract.

Updates `docs/perf-trace-schema.md` and the byte-parity test in `tests/Mithril.Shared.Tests/PerfTracerTests.cs`.

### 5.7 Logging

`CachedBaseTextureProvider` already takes `ILogger?`. New `LogInformation` on successful alpha load (mirrors gray-pixel load); `LogWarning` on hash mismatch / gate reject; `LogInformation` on miss.

## 6. Persistence — cache file shape

| File | Format | Status |
|---|---|---|
| `map-texture-<area>.json` | manifest (existing schema) | unchanged |
| `map-texture-<area>.bin` | DeflateStream-compressed gray pixels (existing) | unchanged |
| `map-texture-<area>-alpha.json` | manifest (parallel schema, v1) | **NEW** |
| `map-texture-<area>-alpha.bin` | DeflateStream-compressed alpha pixels | **NEW** |

The cache dir is supplied by the consumer (typically `%LocalAppData%/Mithril/assets/`). No location change.

## 7. Error handling

| Failure | Behavior |
|---|---|
| Sidecar didn't emit alpha (v1 sidecar still installed) | Provider returns `null`. Consumer (mask cache) falls through to fog mask only. Telemetry `texture.alpha.rejected=manifest_missing`. |
| Manifest parse fails (malformed JSON, missing required fields) | Provider returns `null`, `LogWarning`, telemetry `rejected=manifest_missing`. The log message distinguishes "not found" from "malformed" while the tag groups by branch outcome. |
| Blob decompress fails (corrupt DeflateStream, file unreadable) | Provider returns `null`, `LogWarning`, telemetry `rejected=blob_missing`. Same log-vs-tag granularity split as manifest parse. |
| SHA-256 mismatch | Provider returns `null`, `LogWarning`, telemetry `rejected=hash_mismatch`. |
| Canonical-hash gate rejects | Provider returns `null`, `LogWarning`, telemetry `rejected=canonical_gate_reject`. |
| Width × height mismatch with gray companion | Provider returns `null`, `LogWarning`, telemetry `rejected=size_mismatch`. Indicates sidecar bug; flag loudly. |

**All paths fail-soft to `null`. No exception ever propagates out.** Matches the existing gray-pixel contract.

## 8. Testing strategy

| Test | Project | Asserts |
|---|---|---|
| `Sidecar_emits_alpha_blob_for_RGBA_texture` | sidecar tests (cross-repo) | Run decode on a 4×4 RGBA fixture. Assert both `area.bin` and `area-alpha.bin` exist; alpha bytes match expected. |
| `Sidecar_doesnt_emit_alpha_for_RGB_texture` | sidecar tests | Run decode on RGB-only fixture. Assert only `area.bin` (no alpha file). Sidecar logs warning. |
| `CachedBaseTextureProvider_TryGetTextureAlpha_round_trips` | `Mithril.MapCalibration.Tests` | Write a synthetic `<area>-alpha.{json,bin}` pair. `TryGetTextureAlpha("<area>")` returns the expected `GrayImage`. |
| `CachedBaseTextureProvider_TryGetTextureAlpha_returns_null_on_missing_manifest` | same | No alpha files for the area. Provider returns `null`. Verify `LogInformation` fired. |
| `CachedBaseTextureProvider_TryGetTextureAlpha_returns_null_on_hash_mismatch` | same | Write manifest with deliberately-wrong `pixelSha256`. Provider returns `null`. Verify `LogWarning` fired. |
| `CachedBaseTextureProvider_TryGetTextureAlpha_returns_null_on_size_mismatch` | same | Write manifest declaring 100×100 but blob has 50 bytes. Provider returns `null`. Verify `LogWarning` fired. |
| `CachedBaseTextureProvider_TryGetTextureAlpha_canonical_gate_rejects` | same | Provide a `CanonicalAssetHashGate` rejecting the test hash. Provider returns `null`. Verify gate verdict recorded. |
| `CachedBaseTextureProvider_TryGetTextureAlpha_canonical_gate_artifact_key` | same | Verify the gate is called with `"<area>-alpha"` (not `"<area>"`). Assert correct artifact-key construction. |

## 9. Files touched (anticipated)

### 9.1 Mithril (`src/`)

| File | Change |
|---|---|
| `src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs` | Add `TryGetTextureAlpha` to interface. Update XML doc. |
| `src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs` | Implement `TryGetTextureAlpha` per §5.3. |
| `src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json` (embedded resource) | Add `"<area>-alpha"` entries alongside existing `"<area>"` entries. No code-side schema change. |
| `src/Mithril.MapCalibration.Detection/Internal/DetectionJsonContext.cs` | Add `JsonSerializable` for the alpha manifest type (same shape as the gray manifest). |

### 9.2 Sidecar (cross-repo)

| Change | Where |
|---|---|
| `--maps` decode flow emits alpha cache file alongside gray | sidecar `Program.cs` / `MapDecoder.cs` (locate via #931) |
| Manifest writer for alpha | mirror existing manifest writer |
| Tests for RGBA → alpha emit | sidecar test project |

### 9.3 Tests (`tests/`)

| File | Change |
|---|---|
| `tests/Mithril.MapCalibration.Tests/Detection/CachedBaseTextureProviderAlphaTests.cs` | **new** — 6 unit tests per §8 (round-trip + 4 null/error paths + canonical-gate artifact-key). |

### 9.4 Docs

- This `spec.md` + sibling `plan.md` (slug: `sidecar-rgba-alpha-surface`).
- Append a row to [`docs/planning/INDEX.md`](../INDEX.md).
- Update `docs/perf-trace-schema.md` with the `texture.alpha.*` tag additions.

## 10. Out of scope

- **The consumer mask computation** ([`map-calibration-deviation-mask-1116`](../map-calibration-deviation-mask-1116/spec.md)) lives there, not here. This spec stops at "alpha is reachable from `IBaseTextureProvider`."
- **Surfacing full RGBA** (R / G / B / A as a single image) to Mithril. Only the alpha channel is surfaced, as a separate single-channel artifact.
- **Sidecar changes beyond the maps path.** Icon-template, npcs-data, other sidecar emits — untouched.
- **Cache invalidation strategy beyond the existing canonical-hash-gate pattern.** If a future PG version ships different textures, the canonical-hash catalogue gets a new entry; existing cache files keyed by `<area>-alpha` stay until manually wiped or until the integrity check fails. Same model as today's gray path.
- **Sidecar repo's own version-bumping / release flow.** Sidecar PR coordinates with this spec by reference; release sequencing lands in plan.

## 11. Verification owed

| Claim | How to verify |
|---|---|
| PG textures actually carry alpha (i.e. `Texture2D.format` includes alpha for the relevant assets). | Sidecar Plan Task 0: inspect 3-5 area textures from a Steam install; check Unity's `TextureFormat` field. If RGBA, alpha is non-trivial. If RGB-only, fall to luminance heuristic in consumer (see consumer spec D3). |
| Alpha is the floor / not-floor signal as expected. | Sidecar Plan Task 1: visual inspection of alpha PNGs for 3-5 areas. Confirm alpha = 0 in "not floor" regions, alpha > 0 in floor regions. If alpha encodes something else (e.g. shadow or premultiplied detail), the consumer mask plan needs adjustment. |
| Alpha bytes round-trip through the sidecar → cache → provider path with byte equality. | `Sidecar_emits_alpha_blob_for_RGBA_texture` + `CachedBaseTextureProvider_TryGetTextureAlpha_round_trips` tests. |
| Provider fails-soft on every malformed-cache path. | Unit tests covering each failure mode in §7. |
| No regression on existing gray-pixel cache reads. | Existing `CachedBaseTextureProvider` tests must continue to pass. |

## 12. Cross-references

- [`map-calibration-deviation-mask-1116/spec.md`](../map-calibration-deviation-mask-1116/spec.md) — the consumer of this work.
- [mithril#931](https://github.com/moumantai-gg/mithril/issues/931) ([PR #932](https://github.com/moumantai-gg/mithril/pull/932)) — sidecar foundation.
- [`docs/planning/calibration-1081-overlay-cross-frame-composition/spec.md`](../calibration-1081-overlay-cross-frame-composition/spec.md) — established the `CanonicalAssetHashes` v1 → v2 schema bump pattern this spec extends.
