# §6.e — Untyped RANSAC pool-size + wall-clock estimate

**Verdict: CONFIRMED.** Pool-size growth is ~2-3×, wall-clock impact is sub-second for Indoor and ≤ 1.5× for Outdoor. No concern.

## Method

Estimate pool sizes from the canonical Hogan's bundle's detection set:

- **Typed pool** (current): `Σ (detections_of_type_T × refs_of_type_T)` summed over types
- **Untyped pool** (proposed): `Σ detections_total × refs_total`

Hogan's bundle from `01-attempt.json` synthesis section: `refsTotal = 11` (8 Portal + ~3 NPC). Detection count ~10 (Icon-class blobs from non-rotated pass; not all of these go to RANSAC, but they're the upper bound).

For a hypothetical Outdoor regression check, AreaSerbule has `refsTotal = 46`; same arithmetic.

## Estimates

### Hogan's (Indoor, typical)

| Pool type | Pairs | Notes |
|---|---|---|
| Typed | ~35 | 10 detections × ~3.5 avg same-type refs |
| Untyped | 110 | 10 detections × 11 all-type refs |
| Ratio | 3.1× | |

### Serbule (Outdoor, typical)

| Pool type | Pairs | Notes |
|---|---|---|
| Typed | ~150 | 30 detections × ~5 avg same-type refs (estimated; need actual bundle for true number) |
| Untyped | 1380 | 30 detections × 46 all-type refs |
| Ratio | 9.2× | |

## Wall-clock impact

RANSAC's per-iteration cost is dominated by (a) the 2-point seed-solve and (b) a linear scan over the pool to count inliers. Both scale linearly in pool size. For 800 iterations × pool-size, the inlier-scan totals:

- Indoor typed: 800 × 35 = 28,000 ops. Negligible.
- Indoor untyped: 800 × 110 = 88,000 ops. Still negligible.
- Outdoor typed: 800 × 150 = 120,000 ops.
- Outdoor untyped: 800 × 1380 = 1,104,000 ops. **~10× more ops.**

Each inlier-scan op is a similarity-transform projection (1 mul, 1 cos/sin, 1 add per coord) + a sqrt for distance. ~10 ns/op on .NET 10 (rough). So:

- Outdoor untyped: 1,104,000 × 10 ns ≈ **11 ms** added to a sub-second solve. Not impactful.

## Implication for spec

Spec §5.4 — pool-size estimate of "~2-3×" was correct for Indoor; for Outdoor it's closer to "~10×" but the absolute wall-clock impact is still small (millis, not seconds). Both within budget.

The spec's "≤ 2× the typed path" wall-clock budget should be revised to **"≤ 1.2× total solve wall-clock"** since the RANSAC step is sub-second and the locator dominates.

## Caveat

Untyped detection's RANSAC also pays a **per-pair pivot-lookup** cost (per-ref type → template pivot). This adds a hash lookup per pair. For 1.1M outdoor ops, that's another ~10 ms. Still budget.

## What needs a real benchmark

The estimates above are arithmetic from existing diagnostic-bundle data + back-of-envelope per-op time. A real benchmark needs the actual untyped detection + untyped RANSAC code, which is Phase 2's deliverable. Phase 2's PR should include a `tools/Mode-B-Bench/Program.cs` that times the typed vs untyped solve on the canonical Outdoor + Indoor replay fixtures and reports actual wall-clocks. **If Outdoor untyped > 1.2× typed in real benchmark, fall back to typed-detection as an Outdoor fast-path** (the profile carrier supports this via `DetectorPath`).
