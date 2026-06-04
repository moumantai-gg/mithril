# HogansKeep-223119 corpus regression — local-only fixture

The `Recovers_HogansKeep_223119_truth_from_corpus_bundle` test in
[`SobelPaddedPyramidRefinerTests.cs`](../../SobelPaddedPyramidRefinerTests.cs)
locks the mithril#1061 Sobel-padded-pyramid algorithm against drift on the
canonical round-5 corpus bundle.

The capture is a screenshot of Project Gorgon's in-game map UI and the base
texture is decoded PG art — both copyrighted, **neither shippable in the repo**.
The fixture loader ([`HogansKeepCorpusFixture.cs`](../HogansKeepCorpusFixture.cs))
reads both from the developer's local `%LocalAppData%/Mithril/` paths when
available, and the test early-returns on a clean checkout where the corpus is
absent. CI (and any contributor without PG installed) sees the test pass as a
no-op; the regression locks in for anyone who can reach the bundle locally.

This directory is intentionally near-empty: it holds the README and is the
documented landing zone if a future, repo-shippable substitute fixture is ever
constructed (e.g. a synthetic capture deterministically built from public
inputs).

## How to populate it locally

1. Launch Mithril against Project Gorgon (the Steam install).
2. Load HogansKeepBasement and bring up the in-game map at a zoom that puts
   the texture at ~720×720 displayed pixels.
3. Trigger an auto-calibrate attempt (default hotkey or the manual draw → snip
   path). The diagnostic bundle lands at
   `%LocalAppData%/Mithril/diagnostics/calibration/Map_HogansKeepBasement-<UTC-timestamp>-<outcome>/`.
4. The asset-extractor sidecar (mithril#931) populates the base texture at
   `%LocalAppData%/Mithril/assets/map-texture-Map_HogansKeepBasement.{bin,json}`.

The fixture loader searches for `Map_HogansKeepBasement-20260603-223119-*`
specifically — that timestamp is the round-5-comment-cited bundle whose
GIMP-aligned truth `(126, 35, 0.7227)` is hard-coded in the regression test's
expected ranges. A different timestamp's bundle won't share that ground truth.

## Why not check in a downscaled / quantised / watermarked variant

That's a reasonable future direction. For v1 the regression's value lives on
the original-resolution capture (sub-pixel scale recovery + parabolic peak
refinement both leak signal at the texture's full gradient). A derivative
fixture would need its own bespoke truth values and would no longer carry the
exact `round-5 comment ↔ regression test` traceability.

## Related

- mithril#1061 — the issue this regression locks against.
- `docs/planning/map-calibration-sparse-locate-fallback-1061/` — spec + plan.
- `Fixtures/CalibrationBundles/` in this same directory — uses a similar
  layout but **does** check in PG art. That precedent predates this fixture
  and is in tension with the principle here; surface as a follow-up cleanup
  if the policy is consistent.
