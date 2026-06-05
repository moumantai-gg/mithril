# Plan — mithril#1082 frame-aware trigger + storage

**Spec:** [spec.md](spec.md). **Issue:** [#1082](https://github.com/moumantai-gg/mithril/issues/1082).

## Approach

Single PR. Scope is internally cohesive (storage shape + migration + two consumer fixes + one chip-text deletion) and splitting it would create churn (migration tests that exercise the new shape can't land before the shape exists; consumer fixes that read the new shape can't land before the service surface supports it). Phasing below is task ordering, not PR boundaries.

CI gates per task: `dotnet build Mithril.slnx` clean, then `dotnet test Mithril.slnx` clean. The build hook (`check-mithril-running.ps1`) blocks builds while Mithril.exe runs — close it before the test runs.

## File touch budget

| File | Lines changed | Why |
|---|---|---|
| `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs` | ~80 (Load migration v2→v3, Save/Remove signatures, in-memory shape change) | Storage layer |
| `src/Mithril.MapCalibration/Internal/SceneRefinements.cs` | ~50 (new file) | Typed slot record |
| `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs` | ~3 (new `[JsonSerializable]` entries; `UserRefinementFile.Calibrations` field type change) | Source-gen registration |
| `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs` | ~15 (`PickByFrame` reads `slots.Get(frame)`; `GetAllSources` flatten slots; `DeleteUserRefinement` impl) | Service surface |
| `src/Mithril.MapCalibration/IMapCalibrationService.cs` | ~8 (add `DeleteUserRefinement(scene, frame)` signature + XML doc) | Interface |
| `src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs` | ~6 (predicate + variable rename + log message tweak) | Consumer fix |
| `src/Mithril.MapCalibration.Capture/ManualCalibrationCoordinator.cs` | ~10 (routing on `GetTextureCalibration`; `NoTextureFrameRecord` race-fallback) | Consumer fix |
| `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs` | -3 (delete `DriftCheckNoTextureFrameRecord()`) | Chip text cleanup |
| `tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStorePerFrameTests.cs` | ~150 (new file) | Storage round-trips + migration |
| `tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStoreMigrationTests.cs` | ~30 (extend with v2→v3 cases) | Migration |
| `tests/Mithril.MapCalibration.Tests/MapCalibrationServiceTests.cs` | ~30 (extend with two-frames-per-scene + DeleteUserRefinement cases) | Service surface |
| `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationTriggerTests.cs` | ~30 (overlay-frame-doesn't-block regression; BundledBaseline-skip case) | Trigger regression |
| `tests/Mithril.MapCalibration.Capture.Tests/ManualCalibrationCoordinatorTests.cs` | ~40 (frame-specific routing; NoTextureFrameRecord race-fallback) | Coordinator routing |
| `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs` | ~10 (extend `FakeMapCal` to seed per-frame slots if not already supported) | Test fixture |

Total: roughly 450 lines added / 50 removed.

## Phase A — Storage layer

### A.1 — Add `SceneRefinements` typed-slot record

**File:** `src/Mithril.MapCalibration/Internal/SceneRefinements.cs` (new)

**What:** Implement the record exactly as spec §3.1. Internal, sealed, init-only-by-`with`. Methods: `Get(frame)`, `With(frame, cal)`, `Without(frame)`, `IsEmpty`.

**Acceptance:** Builds. No test yet — exercised by A.4 storage tests.

### A.2 — Register `SceneRefinements` in JSON source-gen context

**File:** `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs`

**What:**
- Add `[JsonSerializable(typeof(SceneRefinements))]` and `[JsonSerializable(typeof(Dictionary<string, SceneRefinements>))]`.
- Change `UserRefinementFile.Calibrations` field type from `Dictionary<string, AreaCalibration>` to `Dictionary<string, SceneRefinements>`.

**Acceptance:** Builds. Source-gen produces serializer for the new types. `dotnet build Mithril.slnx` — 0 warnings.

### A.3 — `UserRefinementStore` per-frame storage + migration

**File:** `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs`

**What:**
- Change `_refinements` field type to `Dictionary<string, SceneRefinements>`.
- Rewrite `TryGet`: add `CalibrationFrame frame` parameter; return `slots.Get(frame)`. Keep frame-agnostic `TryGet(areaKey, out)` overload for callers that want "any record" (still used by the all-callers iteration in `AllCalibrations`)? Reconsider: `MapCalibrationService.PickByFrame` is the only production reader and it always knows the frame. Drop the frame-agnostic overload; if a test fixture needs it, expose `TryGetAny`.
  - **Decision:** Drop the frame-agnostic `TryGet`. Tests that need "is there anything for this scene" use the new `All` (returns `IReadOnlyDictionary<string, SceneRefinements>` — preserves the typed shape so the test can inspect both slots).
- Rewrite `Save(string areaKey, AreaCalibration calibration)`:
  - Read `calibration.Frame`; route to the appropriate slot in a new-or-existing `SceneRefinements`.
  - Preserve the existing transactional Persist + rollback discipline.
  - Preserve the defensive `Source` stamp (`AutoCapture`/`UserRefinement` verbatim, everything else → `UserRefinement`).
- Add `Remove(string areaKey, CalibrationFrame frame)` for `DeleteUserRefinement`:
  - Reads existing slots; if the named slot is null, return false (idempotent).
  - Sets the slot to null via `slots.Without(frame)`.
  - If the resulting `SceneRefinements.IsEmpty`, remove the scene entry from the dict.
  - Transactional Persist + rollback.
- Keep frame-agnostic `Remove(string areaKey)` for `ClearUserRefinement(scene)`: removes the entry entirely.
- Rewrite `Load`:
  - Read `schemaVersion` (default 1 if absent).
  - **v3 path:** deserialize directly into `Dictionary<string, SceneRefinements>`. No migration.
  - **v2 path:** for each `(MapAssetKey, AreaCalibration)`, place under the slot named by `cal.Frame`. Apply the narrow-window fix-up (§4.1): if `Source == UserRefinement && Frame == Texture`, log warning and route to Overlay slot instead.
  - **v1 path:** run the existing v1→v2 step (prefix `Map_`, Source-based Frame inference), then run the v2→v3 step on the result.
  - After v1→v3 or v2→v3, persist immediately as v3. Transactional rollback on Persist failure (matches existing v1→v2 discipline).
- Preserve per-entry resilient parse: an unparseable single entry skips with a warn-log, the rest of the file loads.

**Acceptance:** Builds. Tests in A.4 pass.

### A.4 — Storage round-trip + migration tests

**File:** `tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStorePerFrameTests.cs` (new)

**Cases:**
1. `Save_TextureThenOverlay_SameScene_BothCoexist` — save two records on `Map_X` with different Frames; `All` returns one entry whose `SceneRefinements` has both slots populated.
2. `Save_TextureTwice_SameScene_SecondReplacesInTextureSlot` — overlay slot stays null.
3. `Save_PreservesSourceStamp` — saving a record with `Source: BundledBaseline` gets restamped to `UserRefinement` (existing defensive behavior); `Frame` untouched.
4. `Remove_FrameAgnostic_RemovesEntireSceneEntry` — `Remove(areaKey)` with both slots populated → entry gone.
5. `Remove_FrameScoped_LeavesOtherSlotIntact` — `Remove(areaKey, Texture)` with both slots populated → Texture null, Overlay present.
6. `Remove_FrameScoped_CompactsWhenLastSlotEmptied` — after removing the last populated slot, the scene entry is removed from the dict.
7. `Save_PersistFailureRollsBackInMemory` — fault the file system after the in-memory mutation; assert the in-memory state matches pre-write.
8. `Roundtrip_v3_WriteThenRead_Idempotent` — write a `SceneRefinements`-shaped file, load, assert deep-equal.

**File:** `tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStoreMigrationTests.cs` (extend)

**Cases:**
9. `Migrate_v2_to_v3_HappyPath` — file `{"schemaVersion": 2, "calibrations": {"Map_X": {"frame": "Texture", "source": "AutoCapture", ...}}}` loads as `{"Map_X": SceneRefinements(Texture: ..., Overlay: null)}`. Rewritten file is v3.
10. `Migrate_v2_to_v3_NarrowWindowFixup` — record with `Source: UserRefinement, Frame: Texture` routes to Overlay slot; warn-log captured.
11. `Migrate_v1_to_v3_Composes` — bare-key `AreaSerbule` no-`frame` `Source: UserRefinement` loads as `{"Map_AreaSerbule": SceneRefinements(Texture: null, Overlay: ...)}`; rewritten as v3.
12. `Load_v3_Idempotent` — pre-migrated v3 file loads and round-trips without rewrite.

**Acceptance:** All A.4 tests pass.

---

## Phase B — Service surface

### B.1 — `MapCalibrationService.PickByFrame` reads typed slots

**File:** `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`

**What:**
- `PickByFrame`: candidates list builds from `_userStore.TryGet(scene.MapAssetKey, frame, out var user)` + `_baseline.TryGetValue(...)` (filtered by `baseline.Frame == frame`). Rest of the residual+source-precedence logic unchanged.
- `GetAllSources`: build flat list `[slots.Texture, slots.Overlay, baseline[key]]` with nulls dropped. Need to add a `_userStore.TryGetAny(scene.MapAssetKey, out SceneRefinements slots)` accessor (or use `_userStore.All` and index). Pick whichever keeps the lock granularity tight.

**Acceptance:** Existing `MapCalibrationServiceTests` + `MapCalibrationServicePickerTests` + `MapCalibrationServiceTypedFrameTests` stay green.

### B.2 — `IMapCalibrationService.DeleteUserRefinement(scene, frame)`

**Files:** `src/Mithril.MapCalibration/IMapCalibrationService.cs`, `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`

**What:**
- Interface: add `void DeleteUserRefinement(MapSceneRef scene, CalibrationFrame frame);` with XML doc noting "removes one frame's record; if the scene's last record is deleted, the scene entry is removed entirely; idempotent if the named slot is already empty."
- Impl: validate `scene.MapAssetKey`, call `_userStore.Remove(scene.MapAssetKey, frame)`, log if removed, raise `Changed` if removed.

**Test (extension to `MapCalibrationServiceTests`):**
- `DeleteUserRefinement_RemovesOneFrame_LeavesOther` — save both, delete Texture, assert `GetOverlayCalibration` non-null + `GetTextureCalibration` null.
- `DeleteUserRefinement_IdempotentOnMissingFrame` — call on a scene with no Texture record → no-op, no `Changed` raised.
- `DeleteUserRefinement_RaisesChangedOnRemoval` — assert event fires once on actual removal.

**Acceptance:** New tests pass; existing tests stay green.

### B.3 — `IMapCalibrationService` test-fakes update

**Files:** `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs`, `tests/Legolas.Tests/Services/AreaCalibrationServiceTests.cs`, `tests/Legolas.Tests/Rendering/MarkerPipelineSnapshotTests.cs`, `tests/Legolas.Tests/Rendering/LegolasCalibrationMarkerSnapshotTests.cs`, `tests/Mithril.Overlay.Tests/Fakes/FakeMapCalibrationService.cs`

**What:** Implement `DeleteUserRefinement(scene, frame)` on every test-fake of `IMapCalibrationService`. Each can be a `throw new NotSupportedException()` stub until a specific test needs it.

**Acceptance:** Build clean; all suites compile.

---

## Phase C — Consumer fixes

### C.1 — `AutoCalibrationTrigger` frame-aware predicate

**File:** `src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs`

**What:** Replace lines 161-167:
```csharp
var sources = _calibrationService.GetAllSources(scene);
var convergedTexture = sources.FirstOrDefault(s =>
    s.Frame == CalibrationFrame.Texture &&
    s.Source is CalibrationSource.AutoCapture or CalibrationSource.BundledBaseline);
if (convergedTexture is not null)
{
    _logger.LogInformation(
        "Auto-trigger skipped for {MapAssetKey}: store has converged texture-frame {Source} record (residual {Residual:0.00}px, refs {Refs}). One-shot-per-install respected.",
        key, convergedTexture.Source, convergedTexture.ResidualPixels, convergedTexture.ReferenceCount);
    ...
```

Rename `converged` → `convergedTexture` throughout the block (lines 162-181). Update the picker-disagreement message text from "trigger respects store" → "trigger respects texture-frame store record."

Update the comment above (lines 156-160) to match: "Skip if the store has a converged texture-frame record (AutoCapture or BundledBaseline) for this scene. Overlay-frame records (Legolas-wizard) do not satisfy the trigger's goal of landing a texture-frame AutoCal record — see mithril#1082."

**Acceptance:** Build clean; C.4 tests pass.

### C.2 — `ManualCalibrationCoordinator` frame-aware routing

**File:** `src/Mithril.MapCalibration.Capture/ManualCalibrationCoordinator.cs`

**What:**
- Rename `stored` → `storedAny` at line 68 (for INFO-log purposes only).
- Add `var textureCal = _calibrationService.GetTextureCalibration(scene);` near line 102 (after the `scene is null` guard).
- Replace the `if (stored is null)` branch at line 103-108 with `if (textureCal is null)`.
- Update the comment to match the spec §7 narrative.
- Rewrite the `case DriftCheckOutcome.NoTextureFrameRecord:` branch (lines 137-144) to the race-fallback shape (spec §7.1):
  ```csharp
  case DriftCheckOutcome.NoTextureFrameRecord:
      // Race: GetTextureCalibration returned non-null pre-check but the engine
      // re-read and saw null. Fall through to solve, matching NoStoredCalibration.
      var fallback = await _runner.TryCalibrateCurrentAreaAsync(ct).ConfigureAwait(false);
      _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(fallback));
      break;
  ```

**Acceptance:** Build clean; C.4 tests pass.

### C.3 — Delete `CalibrationStatusFormatter.DriftCheckNoTextureFrameRecord()`

**File:** `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs`

**What:** Delete the method (~3 lines). Also delete any unit test for the formatter method (likely in `tests/Mithril.MapCalibration.Capture.Tests/CalibrationStatusFormatterTests.cs` if present; grep first). Update the comment at `AutoCalibrationEngine.cs:174` if it mentions the deleted formatter method.

**Acceptance:** Build clean; the now-dead call site at `ManualCalibrationCoordinator.cs:143` is gone (replaced in C.2).

### C.4 — Trigger + coordinator regression tests

**File:** `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationTriggerTests.cs` (extend)

**Cases:**
- `Trigger_StoreHasOverlayFrameUserRefinement_Fires` — seed `GetAllSources` with one record where `Frame: Overlay, Source: UserRefinement`; assert engine invoked (the regression for #1082).
- `Trigger_StoreHasTextureFrameAutoCapture_Skips` — seed with `Frame: Texture, Source: AutoCapture`; assert engine NOT invoked + INFO log emitted.
- `Trigger_StoreHasTextureFrameBundledBaseline_Skips` — seed with `Frame: Texture, Source: BundledBaseline`; assert engine NOT invoked (cold-boot retry-storm prevention).
- `Trigger_StoreHasBothFrames_TextureSatisfied_Skips` — seed both overlay-user + texture-baseline; assert engine NOT invoked.

**File:** `tests/Mithril.MapCalibration.Capture.Tests/ManualCalibrationCoordinatorTests.cs` (extend)

**Cases:**
- `Coordinator_SceneHasOnlyOverlayFrame_RunsSolve` — seed `GetTextureCalibration` returns null + `GetCalibration` returns a UserRefinement record; assert `TryCalibrateCurrentAreaAsync` called, `CheckDriftAsync` NOT called.
- `Coordinator_SceneHasOnlyTextureFrame_RunsDriftCheck` — seed `GetTextureCalibration` non-null; assert `CheckDriftAsync` called.
- `Coordinator_SceneHasBothFrames_RunsDriftCheck` — texture available, drift-check runs.
- `Coordinator_NoTextureFrameRecordRace_FallsThroughToSolve` — engine returns `NoTextureFrameRecord` despite pre-check seeing texture; coordinator calls `TryCalibrateCurrentAreaAsync` as race-fallback. Delete or repurpose the existing `Tier3_NoTextureFrameRecord_Chip` test (the chip-text-asserting one).

**Acceptance:** All new + existing tests pass. Net change to test count is positive (4 new trigger tests + 4 new coordinator tests minus 1 deleted chip test).

---

## Phase D — Verification

### D.1 — Full-suite green

```powershell
dotnet build Mithril.slnx
dotnet test Mithril.slnx
```

Both must report 0 warnings / 0 failures (allowing 3 pre-existing `ReplayFixtureTests` failures that are present on `main` and unrelated to this work — confirm by running on `origin/main` first).

### D.2 — In-game smoke (verification owed §12)

In a development build:
1. **Setup:** start with a fresh `refinements.json` containing only an overlay-frame `Map_KhyruleksCrypt` record (Legolas-wizard-produced). Confirm by inspecting JSON.
2. **Manual hotkey test:** zone into Khyruleks Crypt. Press the manual calibrate hotkey. Expected: chip shows the AutoCal solver outcome (success "Calibrated. ..." or actionable failure). `refinements.json` post-press contains both `texture` and `overlay` slots for `Map_KhyruleksCrypt`.
3. **Auto-trigger test:** zone out (to Eltibule or any other scene), then back into Khyruleks Crypt. Expected: trigger fires (search the log for `"Auto-trigger firing for Map_KhyruleksCrypt"`); chip clears silently on success.
4. **Bundled-baseline-only check:** pick a scene with ONLY a bundled-baseline texture record (e.g., a scene listed in `BundledData/map-calibration-baseline.json` that has no entry in `refinements.json`). Zone into it. Expected: trigger does NOT fire (`"Auto-trigger skipped for ...: store has converged texture-frame BundledBaseline record"`). This is the intentional behavior change noted in spec §10.

### D.3 — `refinements.json` migration smoke

On a developer machine that has been running Mithril through the #1077 → #1083 window:
1. Back up `%LocalAppData%/Mithril/MapCalibration/refinements.json`.
2. Boot Mithril on the #1082 branch.
3. Confirm Mithril didn't crash; check `boot.log` for migration warn-logs (narrow-window fix-up entries are expected on records written between #1077 and #1083).
4. Inspect the rewritten `refinements.json`: `schemaVersion: 3`, each `Map_X` key is a `SceneRefinements` shape, the catalyst `Map_KhyruleksCrypt` record routed to the Overlay slot.

---

## Phase E — Wrap-up

### E.1 — Open PR

Branch + push + `gh pr create` with HEREDOC body. PR title: `fix(map-calibration): per-frame UserRefinementStore + frame-aware trigger/coordinator (closes #1082)`.

PR body sections:
- Summary (one paragraph: the three call-site fixes + storage redesign)
- Verification (link to spec §12; in-game smoke results from D.2; migration smoke from D.3)
- Test plan (bullets from D.1)
- Links: spec + plan + closes #1082 + refs #1077 / #1078 / #1083 / #1081

### E.2 — Flip INDEX status on merge

After merge, separate docs-only commit: flip `docs/planning/INDEX.md` row for `calibration-1082-frame-aware-trigger-storage` from `active` to `shipped`. Add the PR number to the issue/PR column.

---

## Risks (carried from spec §10)

- **`GetAllSources` consumer assumption check.** Done by inspection at spec-write time; rerun grep before C.4 in case anything landed between then and execution. Test fakes return `Array.Empty` so they're safe.
- **Source-gen registration drift.** `SceneRefinements` not in `MapCalibrationJsonContext` causes runtime throw. Mitigated by A.4 round-trip tests.
- **Narrow-window v2 records on dev machines.** Mitigated by §4.1 fix-up + warn-log + D.3 dev-machine smoke.
- **In-flight branch conflict with another calibration PR.** Coordinate with anyone working in `Mithril.MapCalibration` or `Mithril.MapCalibration.Capture`. The file touch budget table is the conflict surface — most likely overlap is `MapCalibrationService.cs` and `AutoCalibrationTrigger.cs`.

## Out of scope (spec §11 — restated for the implementer)

- Don't touch `BundledBaselineLoader` (one slot per scene is fine).
- Don't touch `LegolasSettings.AreaCalibrations` (deprecated; field-removal is a separate PR).
- Don't wire up `DeleteUserRefinement(scene, frame)` callers — it ships unused.
- Don't fix #1081 (Legolas overlay cross-frame composition).
