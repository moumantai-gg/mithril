# Calibration-bundle test fixtures

Replay corpus for `FeatureMatchingRefinerReplayTests` (Task 7) and
`FeatureMatchingNegativeTests` (Task 8).

## Fixtures

### `KurMountains-Live-20260602/`

The live capture from the bundle that motivated issue mithril#1009 / PR #1008:
`%LocalAppData%/Mithril/diagnostics/calibration/AreaKurMountains-20260602-192055-747-rejected-map-not-located/`.

The current NCC ladder fails on this capture at coarse score 0.473 at the
wrong rect (192, 100, 909, 909). Ground truth rect (per PR #1008
investigation): **(159, 82, 971, 973)**. `FeatureMatchingRefiner` must
recover this rect to within +/-2 px.

Area key for `CachedBaseTextureProvider`: `AreaKurMountains` (matches the
`Area` prefix in the texture filename).

### `Eltibule-Accepted-20260602/`

The accepted capture from the bundle:
`%LocalAppData%/Mithril/diagnostics/calibration/AreaEltibule-20260602-031025-989-accepted/`.

Eltibule is the "working zone" where the current NCC ladder produces
correct rects. This fixture serves as a positive control: under feature
matching, the recovered rect should be consistent with the NCC-recovered
rect (within tolerance). Used by replay tests (correct-area positive)
and negative tests (Kur texture vs Eltibule capture rejection).

Area key for `CachedBaseTextureProvider`: `AreaEltibule`.

## File layout per fixture

- `capture.png` - the bundle's `03-screenshot-gray.png` (grayscale 8-bit PNG)
- `map-texture-Area<Zone>.json` - the texture manifest from `%LocalAppData%/Mithril/assets/`
- `map-texture-Area<Zone>.bin` - the deflate-compressed gray-pixel payload

Loaded at test time via `CachedBaseTextureProvider(dir).TryGetBaseTexture(areaKey)`.
