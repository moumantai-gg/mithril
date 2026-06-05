# Frame-aware trigger + storage (mithril#1082)

**Status:** active
**Issue:** [#1082](https://github.com/moumantai-gg/mithril/issues/1082)
**Prerequisites:** [#1077](https://github.com/moumantai-gg/mithril/pull/1077) (pixel-frame typing) shipped; [#1083](https://github.com/moumantai-gg/mithril/pull/1083) (picker reads `AreaCalibration.Frame`) shipped.
**Sibling (out of scope here):** [#1081](https://github.com/moumantai-gg/mithril/issues/1081) — Legolas overlay cross-frame composition.

## 1 — Goal & non-goals

**Goal.** Make it possible for an AutoCalibration (texture-frame) record to land on a scene that already has a Legolas-wizard (overlay-frame) record. Both must coexist in `refinements.json` under the same `MapAssetKey`. The auto-trigger reactivates for scenes with only an overlay-frame record; the manual hotkey routes to the AutoCal solver when no texture-frame record exists.

**Non-goals.**
- **Legolas overlay rendering against a texture-frame record.** That is [#1081](https://github.com/moumantai-gg/mithril/issues/1081) (`ProjectThroughOverlay(MapRect)` plumbing). #1082 makes coexistence possible; #1081 makes the texture record renderable in Legolas. Both are AutoCal release blockers; this issue lands first.
- **Retiring `LegolasSettings.AreaCalibrations`.** Deprecated since [#1041](https://github.com/moumantai-gg/mithril/issues/1041) D6; dual-write paths already gone. Field-removal is a follow-up.
- **Restructuring `BundledBaselineLoader`.** The bundled baseline is read-only, hand-authored, and texture-frame by convention. One-slot-per-scene works; no collision.
- **New end-user UX.** No new chip text, no new settings, no new hotkey. The fix is "the action the chip promises now actually works."

## 2 — Problem statement

After [#1077](https://github.com/moumantai-gg/mithril/pull/1077) honestly surfaced the "no texture-frame record for this scene" condition via the `DriftCheckNoTextureFrameRecord` chip, three call sites collude to make the promised action impossible.

### 2.1 Storage bottleneck

`UserRefinementStore._refinements` is `Dictionary<string, AreaCalibration>` keyed by `MapAssetKey` alone ([`UserRefinementStore.cs:27`](../../../src/Mithril.MapCalibration/Internal/UserRefinementStore.cs#L27)). `Save` is unconditional last-writer-wins per scene ([line 71-97](../../../src/Mithril.MapCalibration/Internal/UserRefinementStore.cs#L71)). An `AutoCal.Save` for `Map_X` clobbers a `Wizard.Save` for `Map_X`. There is no shape in storage that lets both records exist.

### 2.2 Auto-trigger gates on Source, not Frame

[`AutoCalibrationTrigger.cs:161-167`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs#L161):

```csharp
var sources = _calibrationService.GetAllSources(scene);
var converged = sources.FirstOrDefault(s => s.Source is CalibrationSource.UserRefinement or CalibrationSource.AutoCapture);
if (converged is not null) { /* skip */ }
```

Any `UserRefinement` record (including a Legolas-wizard overlay-frame one) blocks the auto-trigger forever on that scene.

### 2.3 Coordinator dispatches frame-blind

[`ManualCalibrationCoordinator.cs:103-108`](../../../src/Mithril.MapCalibration.Capture/ManualCalibrationCoordinator.cs#L103):

```csharp
if (stored is null)
{
    var outcome = await _runner.TryCalibrateCurrentAreaAsync(ct);  // ← only place that runs AutoCal
    ...
}
var drift = await _runner.CheckDriftAsync(ct);  // any existing record → drift check
```

`stored` is `_calibrationService.GetCalibration(scene)`, the frame-agnostic picker. If ANY frame's record exists, the path runs drift check, which then refuses with `NoTextureFrameRecord` on an overlay-frame-only scene.

### 2.4 Why this was latent pre-#1077

Pre-refactor, the manual hotkey on an overlay-frame-stored scene silently misinterpreted the record as texture-frame and ran nonsense drift arithmetic, surfacing "Inconclusive — too few visible landmarks." The honest `NoTextureFrameRecord` chip in #1077 made the impasse visible without fixing the underlying inability.

## 3 — Storage redesign

### 3.1 In-memory shape

`UserRefinementStore._refinements` becomes:

```csharp
private Dictionary<string, SceneRefinements> _refinements = new(StringComparer.Ordinal);

internal sealed record SceneRefinements(
    AreaCalibration? Texture,
    AreaCalibration? Overlay)
{
    public AreaCalibration? Get(CalibrationFrame frame) => frame switch
    {
        CalibrationFrame.Texture => Texture,
        CalibrationFrame.Overlay => Overlay,
        _ => null,
    };

    public SceneRefinements With(CalibrationFrame frame, AreaCalibration cal) => frame switch
    {
        CalibrationFrame.Texture => this with { Texture = cal },
        CalibrationFrame.Overlay => this with { Overlay = cal },
        _ => throw new ArgumentOutOfRangeException(nameof(frame)),
    };

    public SceneRefinements Without(CalibrationFrame frame) => frame switch
    {
        CalibrationFrame.Texture => this with { Texture = null },
        CalibrationFrame.Overlay => this with { Overlay = null },
        _ => throw new ArgumentOutOfRangeException(nameof(frame)),
    };

    public bool IsEmpty => Texture is null && Overlay is null;
}
```

Typed slots make "at most one record per (scene, frame)" a compile-time invariant. Code can't accidentally insert a second Texture record for one scene because there's only one Texture slot.

### 3.2 On-disk JSON schema (v3)

```json
{
  "schemaVersion": 3,
  "calibrations": {
    "Map_KhyruleksCrypt": {
      "texture": { "scale": ..., "frame": "Texture", "source": "AutoCapture", ... },
      "overlay": { "scale": ..., "frame": "Overlay", "source": "UserRefinement", ... }
    }
  }
}
```

A scene with only one frame populated omits the unused slot (the existing `JsonIgnoreCondition.WhenWritingDefault` rule on `MapCalibrationJsonContext` drops null values). The inner record's `frame` field is preserved verbatim — it's load-bearing for `MapCalibrationService.PickByFrame` reading `cal.Frame` directly.

### 3.3 Source-gen context updates

[`MapCalibrationJsonContext`](../../../src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs) gains:

```csharp
[JsonSerializable(typeof(SceneRefinements))]
[JsonSerializable(typeof(Dictionary<string, SceneRefinements>))]
```

`UserRefinementFile` becomes `record UserRefinementFile(int SchemaVersion, Dictionary<string, SceneRefinements> Calibrations)`. The bundled-baseline shape is untouched.

### 3.4 `MapCalibrationService.PickByFrame`

Today reads the one user-store record and filters `user.Frame == frame`. After: reads `slots.Get(frame)` directly. The candidates list now consists of (a) `slots.Get(frame)` if non-null, (b) the bundled-baseline record if it matches the frame. Residual+source-precedence tie-break unchanged.

`GetAllSources(scene)` returns a flattened list: `[slots.Texture, slots.Overlay, baseline[key]]` with nulls dropped. The list can now contain up to three records (previously up to two). Existing callers iterate; none assume count ≤ 2 — verified by inspection.

## 4 — Migration

`UserRefinementStore.Load` reads `schemaVersion` and dispatches:

- **v3** → deserialize directly into `Dictionary<string, SceneRefinements>`.
- **v2** → for each `(MapAssetKey, AreaCalibration)` entry, place the record under the slot named by `cal.Frame`. Persist immediately as v3.
- **v1** → existing v1→v2 step runs first (prefix `Map_`, infer Frame from Source per the [calibration-1076 spec §7.2](../calibration-1076-pixel-frame-typing/spec.md#72-load-time-provenance-fallback-schema-1--2) table). The intermediate v2 result is then run through the v2→v3 step. Single Persist at the end.

### 4.1 Narrow-window v2 fix-up

Between #1077 landing (Schema bumped to 2, `Frame` field added defaulting to Texture) and #1083 landing (save sites started stamping `Frame` explicitly), a v2 record produced by the Legolas wizard could have been persisted with `frame: "Texture"` even though it's geometrically an overlay-frame fit. This is a narrow window (~24 hours, developer environments only — AutoCal has never shipped in a tagged release).

On v2→v3, if a record has `Source: UserRefinement` AND `Frame: Texture`, log a warning and route to the Overlay slot anyway (Source-based inference is more reliable here than the field value). This catches the dev's catalyst Map_KhyruleksCrypt record if it was written in that window.

### 4.2 Idempotence

After v1→v3 or v2→v3 migration runs, the file is rewritten as v3. Subsequent boots see `schemaVersion: 3` and skip both migrations. Same transactional Persist used today — if it throws, in-memory state is rolled back to empty (matches `Save`'s rollback discipline).

### 4.3 Forward-compat

A v3 record loaded by a pre-refactor build would fail to deserialize (the inner `SceneRefinements` shape is not an `AreaCalibration`). Downgrades aren't anticipated; the user store is per-user and re-creatable. Acceptable for the same reason #1077 accepted: AutoCal hasn't shipped, no end-user data is at risk.

## 5 — `IMapCalibrationService` API surface

| Method | Status | Behavior |
|---|---|---|
| `SaveUserRefinement(scene, calibration)` | unchanged signature | Routes to the slot named by `calibration.Frame`. Defensive `Source` stamp preserved; `Frame` untouched. |
| `ClearUserRefinement(scene)` | unchanged signature, unchanged behavior | Removes the scene's entry entirely (both slots). Wizard reset preserves today's "starting over for this scene" semantics. |
| `DeleteUserRefinement(scene, frame)` | **NEW** | Removes one slot. If the remaining `SceneRefinements` `IsEmpty`, the scene entry is removed (compaction matches v2→v3 invariant). No production caller in #1082; ships for parity with the per-frame storage shape. |
| `IsCalibrated(scene)` | unchanged | Returns true if any record exists for the scene. |
| `GetCalibration(scene)` | unchanged | Frame-agnostic picker; same residual+source-precedence tie-break, now picks among `slots.Texture` + `slots.Overlay` + `baseline`. |
| `GetTextureCalibration(scene)` / `GetOverlayCalibration(scene)` | unchanged behavior | Source from typed slots instead of "filter the single user record by `cal.Frame`." |
| `GetAllSources(scene)` | unchanged signature; list size can now reach 3 | Returns `[texture-user, overlay-user, baseline]`, nulls dropped. |

## 6 — `AutoCalibrationTrigger` gate

[`AutoCalibrationTrigger.cs:161-167`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs#L161) — change the converged-record predicate:

```csharp
var sources = _calibrationService.GetAllSources(scene);
var convergedTexture = sources.FirstOrDefault(s =>
    s.Frame == CalibrationFrame.Texture &&
    s.Source is CalibrationSource.AutoCapture or CalibrationSource.BundledBaseline);
if (convergedTexture is not null) { /* skip */ }
```

- `Frame == Texture` is the load-bearing axis: the trigger's job is to land a texture-frame AutoCal record; an overlay-frame wizard record doesn't satisfy that goal.
- `Source is AutoCapture or BundledBaseline` — both infer to Texture today, but reading Source-side keeps "one cold solve per install" honest: a BundledBaseline texture anchor IS a converged texture state and the trigger should respect it. Without this, AutoCal would retry every scene with a baseline anchor on every cold boot (the `_persistedScenes` cache prevents in-session retries but not cross-restart ones).
- `_persistedScenes` HashSet + `_inFlightScenes` guard logic unchanged — both key on `MapAssetKey` and are agnostic of frame.
- The picker-store-disagreement INFO-log block at lines 169-181 needs only a variable rename (`converged` → `convergedTexture`); the comparison `picked.Source != converged.Source` is still meaningful.

**INFO log on skip:** updated to mention frame for diagnostic clarity:
```
"Auto-trigger skipped for {MapAssetKey}: store has converged texture-frame {Source} record (residual {Residual:0.00}px, refs {Refs}). One-shot-per-install respected."
```

## 7 — `ManualCalibrationCoordinator` routing

[`ManualCalibrationCoordinator.cs:103-108`](../../../src/Mithril.MapCalibration.Capture/ManualCalibrationCoordinator.cs#L103) — route on frame-specific record:

```csharp
var textureCal = _calibrationService.GetTextureCalibration(scene);
if (textureCal is null)
{
    // No texture-frame record exists — run AutoCal solve to land one, even if an
    // overlay-frame record (from the Legolas wizard) is already stored. The
    // SceneRefinements slots let both coexist after the solve persists.
    var outcome = await _runner.TryCalibrateCurrentAreaAsync(ct);
    _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(outcome));
    return;
}
var drift = await _runner.CheckDriftAsync(ct);
```

The frame-agnostic `stored` local (line 68) stays for the INFO-log line; rename to `storedAny` for clarity. The frame-specific check `textureCal` is what decides the AutoCal-vs-drift routing.

### 7.1 `NoTextureFrameRecord` race-fallback

After this fix, `CheckDriftAsync` is never called without a texture-frame record being present. The `DriftCheckOutcome.NoTextureFrameRecord` branch in the switch becomes a race-fallback — matching the existing `NoStoredCalibration` race-fallback shape (the stored record existed at our pre-check but the engine re-read and saw null):

```csharp
case DriftCheckOutcome.NoTextureFrameRecord:
    // Race: GetTextureCalibration returned non-null pre-check but the engine
    // re-read and saw null. Fall through to solve, matching NoStoredCalibration.
    var fallback = await _runner.TryCalibrateCurrentAreaAsync(ct).ConfigureAwait(false);
    _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(fallback));
    break;
```

## 8 — Chip text cleanup

- **Delete `CalibrationStatusFormatter.DriftCheckNoTextureFrameRecord()`** + its tests. The chip "No AutoCalibration record for this scene — press AutoCalibrate to land one" — the chip the issue title quotes — disappears with it.
- **No new chip is needed.** After the fix, on a scene with only a wizard record: hotkey press → `textureCal` is null → AutoCal solver runs → chip shows the standard `ForOutcome` (success "Calibrated. Scene Map_X, residual N px" or actionable failure like "Map not located. Open it on screen and re-press."). No bespoke "land an AutoCal record" message — the action just happens.
- **Auto-trigger silent-upgrade path unchanged.** On a scene with only an overlay-frame record, the next zone-in fires the trigger (no longer blocked), runs the solve, persists alongside, and clears the chip silently via the existing line 205 behavior.

## 9 — Test strategy

### 9.1 Storage-layer round-trips (`UserRefinementStoreFrameInferenceTests` extensions + new file)

- Save `frame=Texture` record A, save `frame=Overlay` record B for the same scene → both come back via `TryGet(scene, Texture)` and `TryGet(scene, Overlay)`. Neither is overwritten.
- Save `frame=Texture`, save another `frame=Texture` on the same scene → the second replaces the first in the Texture slot; the Overlay slot stays null.
- `ClearUserRefinement(scene)` with both slots populated → both slots null; scene entry removed from dict.
- `DeleteUserRefinement(scene, Texture)` with both slots populated → Texture slot null; Overlay slot intact. Then `DeleteUserRefinement(scene, Overlay)` → scene entry removed.

### 9.2 Migration round-trips

- **v1 → v3**: bare key `AreaSerbule` with no `frame` field, `Source: UserRefinement` → loads as `{"Map_AreaSerbule": {"overlay": {...}}}`. Persisted v3 reads back idempotent.
- **v2 → v3 happy path**: `{"Map_X": {"frame": "Texture", "source": "AutoCapture", ...}}` → `{"Map_X": {"texture": {...}}}`.
- **v2 → v3 narrow-window fix-up**: `{"Map_X": {"frame": "Texture", "source": "UserRefinement", ...}}` → routes to Overlay slot with warn-log.
- **v3 → v3 no-op**: already-migrated file loads without rewriting.

### 9.3 `AutoCalibrationTriggerTests` regression

- Store has an overlay-frame `UserRefinement` record for the scene → trigger fires (regression for the bug this issue closes).
- Store has a texture-frame `AutoCapture` record → trigger skips.
- Store has a texture-frame `BundledBaseline` record → trigger skips (cold-boot retry-storm prevention).
- Store has overlay-frame + texture-frame both → trigger skips (texture is converged).

### 9.4 `ManualCalibrationCoordinatorTests` routing

- Scene with only overlay-frame stored → hotkey press → `TryCalibrateCurrentAreaAsync` called (not `CheckDriftAsync`).
- Scene with only texture-frame stored → hotkey press → `CheckDriftAsync` called.
- Scene with both → hotkey press → `CheckDriftAsync` called (texture-frame exists, drift check is meaningful).
- `NoTextureFrameRecord` race-fallback: fake `CheckDriftAsync` returns `NoTextureFrameRecord` even though `GetTextureCalibration` returned non-null → coordinator falls through to solve.

### 9.5 Existing tests stay green

- `MapCalibrationServiceTypedFrameTests` (added in #1083) — picker behavior is unchanged from the consumer's perspective; tests should pass without modification.
- `MapCalibrationServicePickerTests`, `MapCalibrationServiceTests` — `GetCalibration` semantics unchanged.

## 10 — Risk surface

**Dev's existing `refinements.json` on v2.** Loads through the v2→v3 migration. If the dev's catalyst `Map_KhyruleksCrypt` record was written between #1077 and #1083 with `Frame=Texture` + `Source=UserRefinement`, the narrow-window fix-up (§4.1) routes it to Overlay. If it was written post-#1083 with `Frame=Overlay`, the happy path handles it. No data loss either way.

**`GetAllSources` list-size invariant.** Up to 3 records (was up to 2). Verified by inspection that existing callers iterate without count assumptions — `AutoCalibrationTrigger` uses `FirstOrDefault`, the picker uses `OrderBy`, both compose fine with longer lists.

**Race-fallback symmetry.** The `NoStoredCalibration` and `NoTextureFrameRecord` race-fallbacks now both silently re-solve. If a real bug causes the engine to consistently report `NoTextureFrameRecord` after a pre-check that saw a texture record, the coordinator will silently re-solve over and over. Mitigation: the AutoCal solver itself has the `_persistedScenes` per-session guard against retry storms; coordinator-side has no such guard but the hotkey is user-initiated (no zone-in firing). Worst case: each manual press re-solves. Acceptable.

**Bundled-baseline-only scenes.** With the trigger now skipping on `BundledBaseline` (cold-boot retry-storm prevention), a scene with ONLY a bundled-baseline texture record never gets an AutoCal upgrade. This is a behavior change vs today, where `GetAllSources` would not return a `UserRefinement`/`AutoCapture` and the trigger would attempt. **Judgment:** the bundled baseline is intentionally "good enough for v1 release"; AutoCal can still upgrade if the user invokes the manual hotkey, which runs the solver unconditionally (drift check would just verify the baseline is still locating well). Aligns with the "one cold solve per install" principle.

**Source-gen registration drift.** New `[JsonSerializable]` entries are easy to forget; if `SceneRefinements` isn't registered, deserialization throws at runtime. Mitigated by the migration round-trip tests, which exercise the full serialize→deserialize path.

## 11 — Out of scope / follow-ups

- **#1081 — Legolas overlay cross-frame composition.** When AutoCal lands a texture-frame record on a scene whose Legolas overlay needs to render, the overlay needs `ProjectThroughOverlay(MapRect)` plumbing. Separate issue.
- **Retiring `LegolasSettings.AreaCalibrations`.** Deprecated since #1041 D6; field-removal is a one-line follow-up.
- **`DeleteUserRefinement` consumers.** No production caller in #1082. If a future flow needs surgical per-frame delete (e.g., "re-run only the wizard for this scene, keep the AutoCal"), the API is there.
- **Bundled-baseline per-frame storage.** Single-slot is fine today; revisit if a developer ever wants to ship a hand-authored overlay-frame baseline anchor (no current intent).

## 12 — Verification owed

- [ ] **End-to-end behavioral (in-game)**: Start with a scene that only has an overlay-frame wizard record (e.g., a fresh Map_KhyruleksCrypt). Press the manual hotkey → AutoCal solver runs (not drift check) → on success, `refinements.json` contains both a `texture` slot (AutoCapture) and an `overlay` slot (UserRefinement) for the scene.
- [ ] **Auto-trigger reactivation (in-game)**: Same starting state, zone out and back in → trigger fires (no longer skipped by the overlay-frame record), AutoCal attempt persists, chip clears silently.
- [ ] **Migration on the dev's actual `refinements.json`**: confirm the file loads cleanly, the catalyst record routes to the expected slot (Overlay), and the rewrite-to-v3 doesn't lose other entries.
- [ ] **`GetAllSources` callers spot-check**: grep usages, confirm none assume count ≤ 2. Inspection during spec-write found no offenders, but verify under live build.
- [ ] **Bundled-baseline-only scene check**: confirm a scene with ONLY a bundled baseline does NOT trigger AutoCal on zone-in (intentional behavior change vs today). Spot-check one such scene in `BundledData/map-calibration-baseline.json`.

---

*Drafted by Claude (Opus 4.7) during the 2026-06-05 brainstorming session, posted by @arthur-conde.*
