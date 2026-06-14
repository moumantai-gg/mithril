# Mode-B Phase 0 spike measurements

Phase 0 of the [implementation plan](../plan.md). Validates spec §6 verification-owed items against the existing diagnostic-bundle corpus *before* code lands.

| File | §6 item | Verdict | Surprise? |
|---|---|---|---|
| [scene-class-classification.md](scene-class-classification.md) | §6.a — alpha-coverage threshold | **CONFIRMED** | No |
| [indoor-render-size.md](indoor-render-size.md) | §6.b — Indoor `RenderSizePx` | **REVISED** (keep 16, not 12) | Yes |
| [indoor-chroma-threshold.md](indoor-chroma-threshold.md) | §6.c — chroma pre-filter | **NEGATIVE** (chroma doesn't separate) | Yes |
| [indoor-synthesis-j-threshold.md](indoor-synthesis-j-threshold.md) | §6.d — synthesis-J jMin formula | **PARTIAL** (no ground-truth Indoor accepts) | Anticipated |
| [untyped-ransac-cost.md](untyped-ransac-cost.md) | §6.e — untyped RANSAC pool-size cost | **CONFIRMED** (small) | No |
| [detection-recall-pivot.md](detection-recall-pivot.md) | NEW finding — real-icon recall | **MODE-B PIVOT** | **YES** — load-bearing |

## TL;DR

- §6.a + §6.e — green, spec stands.
- §6.b — spec value 12 was wrong; keep 16. One-line spec edit.
- §6.c (chroma pre-filter) — doesn't work. Spec's Phase 3 ships disabled OR replaces chroma with **peak luma** (a discovered alternative that DOES separate cleanly). Either way, the per-blob feature gate is salvageable.
- §6.d — Indoor synthesis-J has no ground-truth accepts. Phase 4 should ship in Shadow mode for Indoor v1; revisit once Phase 2-3 produce known-good Indoor cals.
- **NEW finding (the big one):** the detector has a **detection-recall failure**, not a detection-precision failure. Of 18 non-rotated Icon-class blobs in the canonical Hogan's bundle, **only 1 contains a real icon glyph**. The other 17 are pure floor-texture noise. Untyped detection (the spec's load-bearing piece, candidate E / Phase 2) does NOT fix this — it changes how RANSAC discriminates type, but if 17/18 blobs are noise, RANSAC has no real correspondences to find regardless of typing strategy. **The Mode-B fix has to start upstream of typing — at deviation+morph+classify — to recover the missing real-icon blobs.** See `detection-recall-pivot.md`.

## What changes in the spec

The scene-class refactor (Phase 1) stands. The Indoor profile divergences (Phases 2-4) need to be re-sequenced:

1. **Phase 2 → "Indoor icon-blob recall"** (new scope). The actual root-cause fix. Probable shape: lower `LowNcc` threshold, weaken the rim/morph filter, OR introduce a chroma/luma-aware deviation kernel. Needs its own design pass.
2. **Phase 3 → Peak-luma pre-filter** (revised from chroma). The empirical finding that PeakLuma > 0.7 cleanly separates real-icon blobs from noise blobs is the noise-suppression mechanism that survives the spike.
3. **Phase 4 → untyped detection + RANSAC type discrimination** (downgraded). Still valuable when Phase 2 succeeds in recovering more real-icon blobs, but no longer load-bearing on its own.
4. **Phase 5 → adaptive synthesis-J Shadow** (deferred from Phase 4 of original plan).

The post-spike spec revision is itself a follow-up — these measurement docs are the input to it. Suggesting the order:

1. Land these measurements as-is (this PR).
2. Open follow-up design pass: "Indoor icon-blob recall — Phase 2 scope" (separate sub-issue under #1155).
3. After that's spec'd, revise the original spec.md / plan.md or supersede with v2.

## Tooling

Measurements ran as ad-hoc PowerShell snippets against bundle artifacts in `%LocalAppData%/Mithril/diagnostics/calibration/` and the asset cache in `%LocalAppData%/Mithril/assets/`. The snippets are not preserved (per spec — spike is throwaway); the measurement docs are the durable record.

For Phase 1 implementation, a `tools/Spike-1155/Program.cs` BCL-only harness can mechanize §6.a (the only measurement that's structurally a recurring computation per scene). The §6.b-e measurements are one-off.
