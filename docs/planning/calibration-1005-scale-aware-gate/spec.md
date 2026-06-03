# Scale-aware monotonicity gate for map auto-calibration (#1005)

**Status:** active · **Issue:** [mithril#1005](https://github.com/moumantai-gg/mithril/issues/1005) · **Follow-up:** [mithril#1006](https://github.com/moumantai-gg/mithril/issues/1006) (per-scale storage)

## Background

The acceptance-gate monotonicity check added in #988 / PR #995 prevents a wrong-fit re-capture from overwriting a good calibration at the **same** in-game zoom (the Eltibule 03:11:05 vs 03:11:30 pair). It compares the new fit's residual and inlier count against the currently-stored calibration:

[`src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs:534-565`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L534-L565)
```csharp
if (existing.ResidualPixels > 0
    && candidate.ResidualPixels > existing.ResidualPixels * 2.0) reject;
if (candidateInlierCount < existing.ReferenceCount - 2) reject;
```

`existing.ReferenceCount` is the **inlier count of the prior fit** (set by `LandmarkCalibrationSolver` at [`LandmarkCalibrationSolver.cs:120`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration/LandmarkCalibrationSolver.cs#L120) as `n` = number of inliers used), so the gate is comparing **new inliers vs prior inliers**, not vs the area's total reference pool.

## Symptom

When the user changes the in-game map zoom between captures, the gate trips on the inlier-delta arm:

- Templates match at `RenderSizePx = 16` ([`AutoCalibrationEngine.cs:56`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L56)).
- A capture at a different zoom has materially different visible-icon size, so the typed-detection count (and therefore the inlier count) is meaningfully different.
- If the existing was at a higher-inlier zoom regime, the new attempt's lower count trips `candidateInlierCount < existing.ReferenceCount - 2` and the new fit is rejected even though it is correct for its zoom.

The status chip then makes the loop user-visible. [`CalibrationStatusFormatter.cs:40-42`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs#L40-L42) maps any reject reason containing "residual" or "inlier" to:

> "Couldn't auto-calibrate — zoom the in-game map all the way out, then redraw the map bbox and retry."

The monotonicity reject contains both substrings, so the user is told to do exactly the thing that just tripped the gate. The instruction and the gate fight each other.

## Why the comparison is wrong across zooms

Residual is computed in **texture-pixel space** (see [`TypeAwareRansacSolver.cs:17`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration/Detection/TypeAwareRansacSolver.cs#L17): "Works in texture-pixel space (via `MapRect.ScreenshotToTexture`) so the inlier predicate is independent of the screenshot's pan/zoom"). The residual arm is zoom-invariant in principle and fine.

The inlier-count arm is not zoom-invariant: visible-icon size determines how many typed detections survive `RenderSizePx = 16` matching, so the inlier count tracks the zoom regime, not the fit quality. Comparing inlier counts across regimes is invalid.

## What we already have

After the PR-4 cutover (#1018), [`FeatureMatchingRefiner`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs) returns a [`MapRegionRefineResult`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs) carrying [`LocateMetrics`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Capture/LocateMetrics.cs). The `LocateMetrics.Scale` field is `√(a² + b²)` decoded from the RANSAC-recovered partial-affine — i.e. the texture→screenshot scale the locator converged on for this capture. Larger = more zoomed in (texture pixels expanded into the screenshot); smaller = more zoomed out.

- **Intrinsic to the capture:** derived from the recovered affine, no PG UI introspection required.
- **Sub-percent stable for repeated captures at the same zoom:** RANSAC + Lowe-ratio on dense ORB features converges very tightly; two captures at the same in-game zoom land within ~1% of each other.
- **Already plumbed:** `AutoCalibrationEngine` already writes `attempt.LocatorMetrics = refineResult.Metrics` for diagnostics ([`AutoCalibrationEngine.cs:283-284`](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L283-L284)).

## Proposed change

Three additive edits, all inside `Mithril.MapCalibration` and `Mithril.MapCalibration.Capture`. **Storage stays single-slot per area for this issue** — multi-slot per-scale storage is the follow-up at [#1006](https://github.com/moumantai-gg/mithril/issues/1006).

### 1. Persist the regime anchor on `AreaCalibration`

Add an additive nullable `LocatorScale : double?` property to `AreaCalibration`. No `SchemaVersion` bump — relies on `JsonIgnoreCondition.WhenWritingDefault` (already configured in `MapCalibrationJsonContext`) + source-gen ignoring unknown properties on read, per the [`CalibrationSource` precedent](https://github.com/moumantai-gg/mithril/blob/main/src/Mithril.MapCalibration/CalibrationSource.cs#L56-L59): downgraded builds ignore the unknown property, upgraded builds read legacy records with the property as `null`. Round-trip safe in both directions.

### 2. Skip the monotonicity check when the scale regime changed

Stamp the candidate's `LocatorScale` from `refineResult.Metrics?.Scale` in `RunAttemptCoreAsync`'s accept path, and wrap the existing `CheckMonotonicAccept` call in an `IsSameScaleRegime(existing.LocatorScale, candidate.LocatorScale)` predicate:

```csharp
// AutoCalibrationEngine.cs around L400 (accept path)
var stamped = result.Calibration with
{
    Source = CalibrationSource.AutoCapture,
    LocatorScale = refineResult.Metrics?.Scale,
};

var existing = _calibrationService.GetCalibration(area);
if (existing is not null
    && IsSameScaleRegime(existing.LocatorScale, stamped.LocatorScale))
{
    var monotonicReason = CheckMonotonicAccept(existing, stamped, result.InlierCount);
    if (monotonicReason is not null) { /* reject as today, with OutcomeCategory */ }
}
// else: cold start OR different scale regime OR either side null → accept unconditionally
```

`IsSameScaleRegime` is a relative-tolerance check: `|c/e − 1| ≤ 0.02` — 2% generous over the locator's sub-percent stability. **Either side null → return false** (skip the gate, accept the new fit). Brainstorming surfaced that "null → same regime" would trap legacy records: a pre-#1005 `null`-stamped record could permanently block a new capture at any other zoom, since the gate's only escape is "better fit at the same regime", but the regime is unknown. `null → skip` lets the first re-capture stamp `LocatorScale` and subsequent comparisons gate normally. Defensive null/finite/positive guards on the values themselves.

The Eltibule 03:11:05 / 03:11:30 pair stays protected: both captures are seconds apart at the same in-game zoom, so their `LocatorScale` values are within tolerance and the monotonicity check still arbitrates.

### 3. Route `RejectedNotMonotonic` away from the "zoom out and retry" chip

Today every reject containing "residual" or "inlier" routes to the same "zoom out and retry" instruction via substring matching on the raw reject reason. **Plan: thread the outcome category structurally.** Add a nullable `OutcomeCategory` field to `AutoCalibrationOutcome` (default `null`), populate it from `OutcomeVocabulary` at every engine return site, and let `CalibrationStatusFormatter.ForOutcome` route on `OutcomeCategory` first, falling back to the existing `ForReject` substring path when null (preserves test surface for any caller that hasn't been updated). The `ForOutcome` route for `RejectedNotMonotonic` is the new message:

> "Calibration unchanged: the new fit was worse than the saved one. To force-replace, clear the saved calibration for this area."

The "clear current area calibration" path already exists via `AreaCalibrationService.ClearCurrentAreaCalibration` (Legolas `CalibrationSessionViewModel`).

## Out of scope (filed as follow-up)

- Per-scale storage (`Dict<area, Dict<scaleBin, AreaCalibration>>`) so a user who routinely calibrates at multiple zoom regimes keeps all of them. v1 is "different regime → fresh fit replaces prior"; v2 keeps both ([#1006](https://github.com/moumantai-gg/mithril/issues/1006)).
- Render-time picking when multiple cals exist for an area.
- Any change to `CalibrationConfidenceGate` thresholds or to the cold-start accept path.
- Migrating other reject reasons to structural routing — the substring fallback in `ForReject` keeps today's behaviour for unrouted categories.

## Definition of done

- `AreaCalibration.LocatorScale` (nullable `double`) added as additive property; JSON-roundtripped; no `SchemaVersion` bump.
- `AutoCalibrationEngine` stamps the candidate's `LocatorScale` from `refineResult.Metrics?.Scale`, threads it into the gate, and bypasses the monotonicity check when `IsSameScaleRegime` returns false (different regime OR either side null OR non-finite/non-positive).
- `AutoCalibrationOutcome` gains an `OutcomeCategory` field (default `null`); every engine return site populates it from `OutcomeVocabulary`.
- `CalibrationStatusFormatter.ForOutcome` routes structurally on `OutcomeCategory` first, falling back to the existing `ForReject` substring path when null. `RejectedNotMonotonic` gets its own user-facing message that doesn't tell the user to do the action that just tripped the gate.
- Unit tests:
  - Same-regime worse fit → still rejected (Eltibule pair protection).
  - Same-regime better fit → accepted.
  - Different-regime new fit → accepted regardless of relative quality.
  - Either-side-null `LocatorScale` → gate skipped; accepts unconditionally.
  - `IsSameScaleRegime` tolerance: at-boundary ±2% both directions; clear out-of-regime; degenerate (NaN / ≤0 / ∞) → false.
- Status-formatter tests:
  - `OutcomeCategory = RejectedNotMonotonic` → new "calibration unchanged" message.
  - `OutcomeCategory = null` + monotonicity-style RejectReason → falls back to today's substring route (regression guard for callers that haven't been updated).
- End-to-end regression test: cold-start cal at one scale → re-capture at a different scale lands without a chip nag; same scale wrong-fit still kept out with the new chip wording.

## Origin

User-reported during PR #995 follow-up:
> "Calibration monotonicity gate prevents calibration from updating if I zoom out"

Conversation surfaced the inlier-vs-existing comparison as the trip and the chip message as the loop.

Brainstorming pass refined three details that landed in this spec:
- Schema is additive (no version bump, per `CalibrationSource` precedent).
- Null `LocatorScale` on either side skips the gate (not "same regime") — addresses the legacy-record trap.
- `OutcomeCategory` is threaded structurally through `AutoCalibrationOutcome` (not substring-matched).

The data path source migrated from `MapRect.SourceScaleFactor` (NCC ladder, deleted in [#1018](https://github.com/moumantai-gg/mithril/pull/1018)) to `LocateMetrics.Scale` (FeatureMatchingRefiner, RANSAC partial-affine). The math is direction-agnostic; the relative-tolerance comparison and null-skip semantics are unchanged.
