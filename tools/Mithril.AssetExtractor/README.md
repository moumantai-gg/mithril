# Mithril.AssetExtractor (`mithril-asset-extract`)

Out-of-process asset-extraction sidecar for [mithril#931](https://github.com/moumantai-gg/mithril/issues/931).

## Why it exists

The shipped app graph (`src/**`) must stay **decoder-free** — no `AssetsTools.NET`,
no `System.Drawing` ([mithril#921](https://github.com/moumantai-gg/mithril/issues/921),
guarded by `ShippedGraphDecoderFreeTests`). This sidecar carries those decoders so
the app never does. It decodes PG's on-disk assets (Unity asset bundles +
`sharedassets0.assets`) in a **child process** and writes a **pre-decoded
manifest+blob cache** the app then loads BCL-only.

The **only** app↔exe link is `System.Diagnostics.Process` (`ProcessAssetExtractor`
in the core) — a process boundary, never a project/package reference. The contract
is the manifest schema + the stdout JSON + the exit code, not an ABI, so the exe is
rewritable in any language without touching the app.

It also replaces the previously-committed PG art (`icon-templates.{json,bin}`):
those are no longer shipped (#931 removed them). The dev commits only validated
SHA-256 **hashes** (`src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json`),
and the sidecar re-derives the pixels at runtime.

## Build

Built outside `Mithril.slnx` (in `tools/Mithril.MapCalibration.Tools.slnx`) to keep
the decoders out of the main solution restore:

```bash
dotnet build tools/Mithril.MapCalibration.Tools.slnx -c Release
```

Published self-contained single-file alongside the shell by the release workflow
(`.github/workflows/release.yml`), so it lands inside the Velopack package next to
`Mithril.exe`. Absent (dev/F5 runs) → the app fail-softs: auto-calibration simply
doesn't run.

## CLI

```
mithril-asset-extract --install <pgRoot> --out <cacheDir> (--icons | --asset <Map_<AssetName>>) [--expect-pg-version <v>]
```

- `--icons` — extract the landmark icon templates from `sharedassets0.assets`
  (needs `classdata.tpk` beside the exe or at the Tools default) and write
  `icon-templates.{json,bin}` into `<cacheDir>`.
- `--asset <Map_<AssetName>>` — extract the literal Unity Texture2D named
  `Map_<AssetName>` (e.g. `Map_AreaSerbule`, `Map_HogansKeepBasement` — the
  verbatim name from the Player.log "Downloading Map" line — see
  [mithril#1021](https://github.com/moumantai-gg/mithril/issues/1021))
  from its Addressables bundle and write the gray-only
  `map-texture-Map_<AssetName>.{json,bin}` into `<cacheDir>`. Renamed from
  `--area` in mithril#1021; the prior flag's value was a bare areas.json key
  (e.g. `AreaSerbule`), which couldn't address aggregator sub-zones whose
  scenes don't appear in `areas.json` (the ~51 dungeons fronted by an
  `AreaCave*` parent).

## Known limitation (v1)

**`--icons` is non-functional in a packaged/released build.** The icon extractor
needs `classdata.tpk` (~290 KB, the Unity class-data package — `sharedassets0.assets`
is type-tree-stripped, so AssetsTools.NET can't decode the Texture2D/Sprite assets
without it). That file is **neither bundled in the csproj nor staged by
`release.yml`**, so in a shipped build `--icons` errors → exit `4` (decode-failed)
→ the app fail-softs (icon templates stay empty → no detections → the confidence
gate rejects → safe-degrade, never a wrong calibration).

`--asset` texture extraction is **unaffected** — per-scene Addressables bundles
ship inline type trees, so no `classdata.tpk` is required.

This is an accepted v1 limitation: icon provisioning (staging `classdata.tpk`
beside the published sidecar, or bundling it) rides with the
[#914](https://github.com/moumantai-gg/mithril/issues/914) PR-2 capture/trigger
work that actually drives `--icons` at runtime. Until then a released `--icons`
run no-ops via the fail-soft chain; a local dev run works when `classdata.tpk`
sits beside the exe (or at the Tools default path).

### Smoke run (against a real PG install)

```bash
# icons
mithril-asset-extract --install "C:\...\Project Gorgon" --out C:\tmp\cache --icons

# one scene's map texture (direct-area)
mithril-asset-extract --install "C:\...\Project Gorgon" --out C:\tmp\cache --asset Map_AreaSerbule

# one scene's map texture (aggregator sub-zone — pre-#1021 the bare AreaCave1
# parent key couldn't address this)
mithril-asset-extract --install "C:\...\Project Gorgon" --out C:\tmp\cache --asset Map_HogansKeepBasement
```

Each run writes the cache files + emits **one JSON result line** on stdout:

```jsonc
{ "status":"ok", "pgVersion":"<detected>", "extractorVersion":"<asm version>",
  "artifacts":[ { "kind":"icons", "area":null, "path":"<cacheDir>/icon-templates.json", "pixelSha256":"…" } ] }
```

`stderr` carries human diagnostics. Exit codes: `0` ok · `2` install-not-found ·
`3` bundle-missing-for-area · `4` decode-failed · `5` output-unwritable.

## Output formats

Both reuse the existing deflate manifest+blob shape the runtime loaders verify:

- **icons** (`icon-templates.{json,bin}`): per icon, `w*h` gray bytes then `w*h`
  alpha bytes, Deflate-compressed; `pixelSha256` over the decompressed stream.
- **texture** (`map-texture-Map_<AssetName>.{json,bin}`): single-entry manifest
  (width, height, pixelSha256) + `w*h` **gray** bytes Deflate-compressed (no
  alpha). The base texture is a single channel the detector diffs the screenshot
  against. Cache key is the literal Unity Texture2D name (with the `Map_`
  prefix) — see [mithril#1021](https://github.com/moumantai-gg/mithril/issues/1021).

`pgVersion` + `extractorVersion` are stamped into every manifest (the
cache-invalidation + canonical-hash-gate keys).
