# blur_aware_corpus

mithril#1070 corpus fixtures for `HogansBlurAwareCorpusTests`. Verifies the
blur-aware Sobel template (applied by `SobelPaddedPyramidRefiner`'s
full-resolution stage) on real Hogan's Basement captures.

## Files

| File | Source | Notes |
|---|---|---|
| `hogans_out_screenshot_gray.png` | `Map_HogansKeepBasement-20260610-154134-968-rejected-solve/03-screenshot-gray.png` | The "zoomed-OUT" Hogan's capture (recovered scale 0.28). The load-bearing failure case the σ-curve targets. |
| `hogans_out_maprect.json` | `…/04-maprect.json` | Per-attempt locator truth for the OUT capture. |
| `hogans_in_screenshot_gray.png` | `Map_HogansKeepBasement-20260610-154213-137-rejected-solve/03-screenshot-gray.png` | The "zoomed-IN" Hogan's capture (recovered scale 0.94). Already-passing case to lock against regression. |
| `hogans_in_maprect.json` | `…/04-maprect.json` | Per-attempt locator truth for the IN capture. |
| `hogans_texture.png` | `%LocalAppData%/Mithril/assets/map-texture-Map_HogansKeepBasement.bin` (deflate-decoded) | Full-resolution decoded Hogan's Basement Map_ texture (1024×1024 gray8 PNG). |

## Why these specific files

`03-screenshot-gray.png` is the raw captured frame (full game render minus
colour), what `SobelPaddedPyramidRefiner.Refine` takes as input. `hogans_texture.png`
is the bundled PG asset the refiner aligns against. The locator-cropped
`06-aligned-screenshot.png` is NOT used here — feeding the refiner the
locator's own recovered crop would be circular logic.

The `04-maprect.json` files carry the auto-cal's recovered (origin, width,
height, textureWidth, textureHeight) — the truth `HogansBlurAwareCorpusTests`
asserts against (within tolerance).

## Regenerating

If the corpus shifts (new PG patch, new evidence bundles), follow the
extraction recipe in `tools/MapCalibrationFromScreenshot/BlurFitSpike/`
which walks the user's `%LocalAppData%/Mithril/diagnostics/calibration/`
directory. The 03/04 files come from the bundle as-is; the texture comes
from the asset cache via the deflate-decode + WPF PNG encoder pattern
documented in `LiveMapViewProbeRealScreenshotBenchmark.cs::TryLoadBaseTexture`.

PG-derived art assets — gray-only no-UI-chrome, no character name, no
session metadata. Safe to commit.
