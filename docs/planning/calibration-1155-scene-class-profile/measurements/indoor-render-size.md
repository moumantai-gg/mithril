# §6.b — Indoor `RenderSizePx`

**Verdict: REVISED.** Spec's proposed `RenderSizePx = 12` for Indoor was wrong. Keep `RenderSizePx = 16` (same as Outdoor).

## Method

The spec proposed `12` from the intuition "indoor icons render smaller." This was an inference, not a measurement. The spike measured by scanning the raw screenshot of the canonical Hogan's bundle (`Map_HogansKeepBasement-20260613-230459-600`) for bright-pixel clusters (`R, G, B > 200`) and measuring their spatial extent.

## Finding

Real-icon bright-pixel clusters in the raw screenshot are 15-16 px in their longest dimension:

| Aligned XY | Cluster size | Visible feature |
|---|---|---|
| (324, 180) | ~16 px | Upper-middle icon (largest cluster, 37+36+14+11 = 98 bright px combined) |
| (411, 185) | ~15 px | Upper-middle icon east |
| (428, 256) | ~15 px | Mid-middle icon |
| (499, 682) | ~13 px | Lower-middle icon (blob 176 in detector output) |
| (374, 669) | ~14 px | Lower-middle icon adjacent |

Indoor render size ≈ 14-16 px = same as Outdoor's `16`.

Why the intuition was wrong: PG renders map icons at a **screen-space** fixed size regardless of in-game map zoom. The texture-space size depends on `mapRect.scale`, but the `RenderSizePx` constant tracks the *on-screen* size, which doesn't change between Indoor and Outdoor.

## Implication for spec

Spec §5.1 — Indoor profile `RenderSizePx` changes from `12` to `16` (= same as Outdoor). One-line spec edit.

Plan Phase 2 — the line "Flip Indoor's `DetectorPath = Untyped`, `TypeFloor = null`, `RenderSizePx = 12`" → drop the `RenderSizePx = 12` clause (no change from Outdoor; the profile carrier still carries the value but it's identical to Outdoor's).
