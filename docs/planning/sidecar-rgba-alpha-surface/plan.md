# Asset sidecar: surface texture alpha — plan

**Spec:** [`spec.md`](spec.md). **Issue:** to be filed; sub-issue of [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116). **Consumer:** [`map-calibration-deviation-mask-1116`](../map-calibration-deviation-mask-1116/spec.md). **Branch posture:** Mithril-side work lands on a feature branch in mithril/main; sidecar-side work lands in the sidecar repo via a coordinated PR referencing this plan.

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task.

Seven tasks. Task 0 is the alpha-presence measurement that gates D3 of the consumer spec. Tasks 1-3 are Mithril-side (consumer-facing surface). Tasks 4-5 are sidecar-side (decode + emit). Task 6 is catalogue updates. Task 7 is verification.

The TDD ordering mirrors prior calibration specs (#1061, #1070, #1123): vocabulary first (interface change, build green), then producer-side (with the unit tests that pin it), then integration.

---

## Task 0 — Measurement: verify alpha-presence in PG textures

**Files:** Throwaway harness under `tools/AlphaSamplingSpike/` — analogous to #1061's `SparseLocateSpike.cs`, deleted when implementation lands.

**Goal:** Verify the consumer spec's D3 assumption that PG's bundled textures carry alpha = 0 in not-floor regions and alpha > 0 in floor regions. If alpha is NOT the floor signal, this plan is invalidated and the consumer spec falls back to its option 2 (luminance heuristic).

**Steps:**

1. Locate 3-5 PG asset bundles. PG's install dir is `<Steam>/steamapps/common/Project Gorgon/WindowsPlayer_Data/`. Asset bundles live under `StreamingAssets/aa/` or similar. Map textures key on `Map_<area>`. (User: confirm the actual path; #931 sidecar already knows it.)
2. Use [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) (already a sidecar dependency per #931) to decode 3-5 areas covering both indoor (Hogan's Basement, GoblinDungeon_TopFloor) and outdoor (Eltibule, Serbule) scenes.
3. For each decoded texture: save as PNG with alpha preserved. Visually inspect: is alpha = 0 (transparent) in the not-floor regions? Is alpha > 0 (opaque or semi-opaque) where the floor is?
4. Record per-area: `alpha-min`, `alpha-max`, `alpha-mean`, `fraction-of-pixels-with-alpha-zero`. If fraction-zero ≈ 0 for any area, that texture has no useful alpha — flag it.
5. **Decision point:**
   - **PASS (alpha is the floor signal):** continue with this plan as written.
   - **FAIL (alpha unusable):** STOP this plan, escalate to consumer-spec author. Consumer spec D3 needs to invert to luminance heuristic.

**Tests:** None (spike). Visual inspection is the verification.

**Acceptance:** Documented per-area alpha distribution (record in this plan + sidecar-readme), one of PASS/FAIL decision recorded.

**Status (2026-06-12, resolved via [#1141](https://github.com/moumantai-gg/mithril/issues/1141)):**

```text
Areas sampled:           5 (3 indoor + 2 outdoor controls)
                         indoor:  HogansKeepBasement (DXT5, 1024x1024)
                                  GoblinDungeon_TopFloor (DXT5, 800x800)
                                  GoblinDungeon main (RGBA32, 398x1024)
                         outdoor: AreaSerbule (RGB24, 1961x2048)
                                  AreaEltibule (RGB24, 2048x2033)

Per-area alpha summary:  Indoor textures carry meaningful alpha
                         (% alpha=0): 81.96% / 72.99% / 91.58%.
                         Visual inspection confirms opaque pixels
                         trace floor extent; transparent = not-floor.
                         Outdoor textures are RGB24 — no alpha
                         channel at all (decoder synthesizes 255).

Decision:                PASS for indoor (Mode-A target).
                         Outdoor RGB24 maps cleanly onto the spec's
                         existing "no alpha → mask null → fog only"
                         safe-degrade path (§7); luminance-heuristic
                         fallback NOT required.
```

---

## Task 1 — `IBaseTextureProvider.TryGetTextureAlpha` interface addition

**Files:** [`src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs`](../../../src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs).

**Steps:**

1. Add the new method to the interface with XML doc:

```csharp
public interface IBaseTextureProvider
{
    GrayImage? TryGetBaseTexture(string mapAssetKey);     // existing

    /// <summary>
    /// The texture's alpha channel for <paramref name="mapAssetKey"/> as a
    /// single-channel <see cref="GrayImage"/>: 0 = transparent (not floor),
    /// 255 = opaque (floor). Same width × height as <see cref="TryGetBaseTexture"/>
    /// for the same key.
    /// </summary>
    /// <returns><see langword="null"/> when the sidecar didn't emit alpha for
    /// this area, the manifest/blob is missing, integrity check fails, or the
    /// canonical-hash gate rejects.</returns>
    GrayImage? TryGetTextureAlpha(string mapAssetKey);
}
```

2. Build the solution. The interface change forces every `IBaseTextureProvider` implementation in the codebase to add the method — find them with `grep`. Add a stub `=> null` body to each (we'll fill in `CachedBaseTextureProvider`'s real body next task; the rest are likely test doubles).

**Tests:**

- The build itself enforces the interface contract. No new dedicated test — the implementations will be covered by their own tests.

**Acceptance:**
- `dotnet build src/Mithril.MapCalibration.Detection/` passes.
- `dotnet build` of the full solution passes.

**Commit:**
```bash
git add src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs
git add <any test stubs added>
git commit -m "feat(map-calibration): add IBaseTextureProvider.TryGetTextureAlpha (sidecar-rgba prereq, sub-issue of #1116)"
```

---

## Task 2 — `CachedBaseTextureProvider.TryGetTextureAlpha` (TDD)

**Files:**
- Modify: [`src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs`](../../../src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs)
- Test: `tests/Mithril.MapCalibration.Tests/Detection/CachedBaseTextureProviderAlphaTests.cs` (new)

**Steps:**

1. **Write the failing round-trip test first.** New test file:

```csharp
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public class CachedBaseTextureProviderAlphaTests
{
    [Fact]
    public void TryGetTextureAlpha_round_trips_bytes()
    {
        using var tmp = TempDir.Create();
        var alphaBytes = new byte[] { 0, 0, 255, 255 };  // 2×2: half not-floor, half floor
        WriteAlphaCache(tmp.Path, "Map_TestArea", 2, 2, alphaBytes);

        var provider = new CachedBaseTextureProvider(tmp.Path, hashGate: null, pgVersion: null, logger: null);
        var alpha = provider.TryGetTextureAlpha("Map_TestArea");

        alpha.Should().NotBeNull();
        alpha!.Width.Should().Be(2);
        alpha.Height.Should().Be(2);
        alpha.Pixels.Should().Equal(alphaBytes);
    }

    private static void WriteAlphaCache(string dir, string area, int w, int h, byte[] pixels)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(pixels));
        var manifestPath = Path.Combine(dir, $"map-texture-{area}-alpha.json");
        var blobPath = Path.Combine(dir, $"map-texture-{area}-alpha.bin");
        var manifestJson = $"{{\"schemaVersion\":1,\"width\":{w},\"height\":{h},\"pixelSha256\":\"{sha}\"}}";
        File.WriteAllText(manifestPath, manifestJson);
        using var fs = File.Create(blobPath);
        using var ds = new DeflateStream(fs, CompressionMode.Compress);
        ds.Write(pixels);
    }

    // …additional helpers as needed…
}
```

(If `TempDir.Create()` doesn't exist in the test project, copy the helper from an existing test file — there's one in the `CachedBaseTextureProvider`-adjacent tests today.)

2. **Run the test, watch it fail** (stub returns null):

```bash
dotnet test tests/Mithril.MapCalibration.Tests/ --filter "FullyQualifiedName~TryGetTextureAlpha_round_trips_bytes"
```

Expected: FAIL with the `alpha.Should().NotBeNull()` assertion (the stub returns null).

3. **Implement `TryGetTextureAlpha` in `CachedBaseTextureProvider`.** Body mirrors the existing `TryGetBaseTexture` (same manifest+blob parse, same SHA-256 verify, same canonical-hash-gate semantics), but with `-alpha` suffix on file paths AND `"<area>-alpha"` artifact key on the gate call:

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
    if (count <= 0 || pixels.Length != count)
    {
        _logger?.LogWarning(
            "Alpha blob length {Len} != width*height={Expected} for {MapAsset} — alpha rejected.",
            pixels.Length, count, mapAssetKey);
        return null;
    }

    if (_hashGate is not null)
    {
        // mithril#1116 prereq: use the existing Check method with the
        // "<area>-alpha" artifact key convention — no API addition needed.
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

4. **Run the test, watch it pass:**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/ --filter "FullyQualifiedName~TryGetTextureAlpha_round_trips_bytes"
```

Expected: PASS.

5. **Commit:**
```bash
git add src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs
git add tests/Mithril.MapCalibration.Tests/Detection/CachedBaseTextureProviderAlphaTests.cs
git commit -m "feat(map-calibration): CachedBaseTextureProvider.TryGetTextureAlpha + round-trip test"
```

---

## Task 3 — Provider error-path tests (TDD-after; round out failure-mode coverage)

**Files:** `tests/Mithril.MapCalibration.Tests/Detection/CachedBaseTextureProviderAlphaTests.cs` (modify).

**Steps:**

Add five additional `[Fact]` tests covering the §7 error matrix from the spec. Each should follow the round-trip test's pattern (write a deliberately-broken cache, assert null + log behavior). Each test method goes into the file from Task 2.

1. **`TryGetTextureAlpha_returns_null_when_manifest_absent`** — no files written; assert null.
2. **`TryGetTextureAlpha_returns_null_when_blob_absent`** — manifest only, no blob; assert null.
3. **`TryGetTextureAlpha_returns_null_on_hash_mismatch`** — write manifest with `pixelSha256: "0000…"`; assert null + warning logged.
4. **`TryGetTextureAlpha_returns_null_on_size_mismatch`** — manifest declares `width: 100, height: 100` but blob is 4 bytes; assert null + warning logged.
5. **`TryGetTextureAlpha_uses_alpha_artifact_key_for_canonical_gate`** — build a `CanonicalAssetHashes` catalogue with `"Map_TestArea"` (gray) and `"Map_TestArea-alpha"` (alpha) entries with KNOWN-DIFFERENT hashes. Assert: (a) calling `TryGetBaseTexture` with the gray hash matching gray entry → succeeds, (b) calling `TryGetTextureAlpha` with the alpha hash matching alpha entry → succeeds, (c) calling `TryGetTextureAlpha` with the alpha hash matching the GRAY entry → fails (uses wrong artifact key for lookup → mismatch → null).

(Use `CanonicalAssetHashGate.FromCatalogue(catalogue, logger)` to construct an in-memory gate; existing test pattern.)

**Run all six tests:**

```bash
dotnet test tests/Mithril.MapCalibration.Tests/ --filter "FullyQualifiedName~CachedBaseTextureProviderAlpha"
```

Expected: all 6 PASS.

**Commit:**

```bash
git add tests/Mithril.MapCalibration.Tests/Detection/CachedBaseTextureProviderAlphaTests.cs
git commit -m "test(map-calibration): cover error paths + canonical-gate artifact-key for TryGetTextureAlpha"
```

---

## Task 4 — Sidecar decode: emit alpha cache file (CROSS-REPO)

**Files:** Sidecar repo (`moumantai-gg/mithril-sidecar` or wherever #931 placed it — confirm the actual repo before starting). Modify the `--maps` decode path.

This task lands in the SIDECAR repo, not Mithril. Coordinate via a separate PR there that references this plan. The Mithril-side tests in Tasks 2-3 will continue to PASS regardless of sidecar progress (they use synthetic fixtures); they don't depend on real sidecar output.

**Steps:**

1. Find the existing `--maps` decode entry point (likely a Program.cs or MapDecoder.cs). It currently decodes Unity Texture2D → grayscale → DeflateStream-compressed binary + manifest. The function signature emits the gray pair.
2. Extend the decode path:
   - Before discarding the alpha channel, capture it.
   - Compute `alphaSha256 = SHA-256(alphaBytes)`.
   - Write `map-texture-<area>-alpha.bin` (DeflateStream-compressed alpha bytes).
   - Write `map-texture-<area>-alpha.json` with `{ "schemaVersion": 1, "width", "height", "pixelSha256": alphaSha256 }`.
3. If the source texture has NO alpha channel (RGB-only Texture2D format), don't emit the alpha pair. Log a warning. The Mithril-side provider already safe-degrades on missing alpha files.

**Tests:** Sidecar test project (whatever convention exists there). Two cases:

- `Sidecar_emits_alpha_blob_for_RGBA_texture` — fixture with RGBA Texture2D. Assert both gray + alpha files written; alpha bytes round-trip.
- `Sidecar_omits_alpha_for_RGB_texture` — fixture with RGB Texture2D. Assert only gray file written; warning logged.

**Acceptance:** Sidecar emits the alpha pair for at least the 3-5 areas sampled in Task 0. Running the sidecar against a real PG install (with the Mithril repo's existing config) populates `%LocalAppData%/Mithril/assets/map-texture-Map_HogansKeepBasement-alpha.{json,bin}` (and similar for other areas).

---

## Task 5 — Canonical-hash catalogue: add `<area>-alpha` entries

**Files:** [`src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json`](../../../src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json) (embedded resource).

This task lands in Mithril, after Task 4 has produced alpha files we can hash.

**Steps:**

1. Run the sidecar (Task 4) against the user's PG install. The output is a populated `%LocalAppData%/Mithril/assets/` cache including the new alpha files.
2. For each `<area>` that has alpha files, capture `pixelSha256` from `map-texture-<area>-alpha.json`'s manifest.
3. Append a new entry to the catalogue per `<PG version>`:

```json
{
  "schemaVersion": 2,
  "perPgVersion": {
    "3.0.0.82+1c3350381f": {
      "Map_HogansKeepBasement":       { "sha": "<existing gray sha>", "width": 1024, "height": 1024 },
      "Map_HogansKeepBasement-alpha": { "sha": "<measured alpha sha>", "width": 1024, "height": 1024 },
      …
    }
  }
}
```

- `schemaVersion` stays at 2.
- The `Width`/`Height` of the alpha entry MUST match the gray entry (same pixel grid). If they don't match, that's a sidecar bug — file a sub-issue.

**Tests:**

- Existing `CanonicalAssetHashesLoader` tests should still pass (no schema change).
- The `TryGetTextureAlpha_uses_alpha_artifact_key_for_canonical_gate` test from Task 3 exercises this conventional layout.

**Commit:**

```bash
git add src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json
git commit -m "data(map-calibration): add <area>-alpha canonical hashes for #1116 prereq"
```

---

## Task 6 — Telemetry tags + doc updates

**Files:**
- [`docs/perf-trace-schema.md`](../../perf-trace-schema.md) (modify)
- [`tests/Mithril.Shared.Tests/PerfTracerTests.cs`](../../../tests/Mithril.Shared.Tests/PerfTracerTests.cs) (modify)

**Steps:**

1. The sidecar load span (already emitted from `CachedBaseTextureProvider` via `LogInformation` + existing telemetry catalog if present) gains two new tag conventions when alpha is loaded:
   - `texture.alpha.available` (bool) — alpha cache present + verified
   - `texture.alpha.rejected` (enum string) — `manifest_missing` / `blob_missing` / `hash_mismatch` / `canonical_gate_reject` / `size_mismatch` / null

Check: does `CachedBaseTextureProvider` currently emit a span? If so, add the tags inside the existing `using var span = …` block. If not (it currently relies on `LogInformation` only), wrap the new alpha-load path in a span:

```csharp
using var span = MapCalibrationDiagnostics.ActivitySource.StartActivity("texture.alpha.load");
span?.SetTag("area", mapAssetKey);
// …existing alpha load logic; set additional tags on outcomes…
span?.SetTag("texture.alpha.available", isAvailable);
if (rejectionReason is not null) span?.SetTag("texture.alpha.rejected", rejectionReason);
```

2. Update `docs/perf-trace-schema.md` with the new span + tags. Find the existing `calibration.*` row block in the catalog table around line 68; add `texture.alpha.load` underneath. Find the detailed tag-list section starting around line 325; add the per-tag descriptions.

3. Update the byte-parity test in `PerfTracerTests.cs` (it pins the schema doc against the canonical vocabulary). Likely a single line change — search for the existing tag list and add the new entries.

**Tests:**

- `dotnet test tests/Mithril.Shared.Tests/ --filter "FullyQualifiedName~PerfTracer"` — passes after schema doc + test are in sync.

**Commit:**

```bash
git add src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs
git add docs/perf-trace-schema.md
git add tests/Mithril.Shared.Tests/PerfTracerTests.cs
git commit -m "telemetry(map-calibration): texture.alpha.load span + tags"
```

---

## Task 7 — End-to-end verification

**Files:** None (verification only).

**Steps:**

1. Trigger an auto-cal on a real area (Hogan's Basement is the target consumer scene).
2. Inspect `%LocalAppData%/Mithril/Logs/` for `Mithril.MapCalibration.Detection` lines:
   - `Loaded alpha for Map_HogansKeepBasement (1024x1024) from <cacheDir> (pixelSha256 verified).` — confirms full path works end-to-end.
3. Manually call `IBaseTextureProvider.TryGetTextureAlpha("Map_HogansKeepBasement")` (via a smoke harness or by setting a breakpoint in the consumer code from Task 2 of the consumer plan). Save the returned `GrayImage` as PNG. Visually confirm the alpha shape matches the floor layout (transparent surrounding, opaque interior).

**Acceptance:**
- Live Mithril boot with a populated sidecar cache produces no warnings about missing/malformed alpha.
- Visual inspection of saved alpha matches Task 0's expectations.

---

## Cross-references

- Consumer spec + plan: [`../map-calibration-deviation-mask-1116/`](../map-calibration-deviation-mask-1116/)
- Sidecar foundation: [mithril#931](https://github.com/moumantai-gg/mithril/issues/931) / [PR #932](https://github.com/moumantai-gg/mithril/pull/932)
- Established schema-version pattern: [#1081](https://github.com/moumantai-gg/mithril/pull/1087)
