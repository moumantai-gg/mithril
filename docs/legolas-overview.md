# Legolas — architectural overview

A bootstrap-context doc for future contributors (human or LLM) starting work on Legolas. Covers Survey mode end-to-end as it works **today**. Motherlode is out of scope here — see [`MotherlodeFlowController`](../src/Legolas.Module/Flow/MotherlodeFlowController.cs) and [`MotherlodeViewModel`](../src/Legolas.Module/ViewModels/MotherlodeViewModel.cs) for that side.

The model has been through a significant rewrite (#454/#460/#476/#477/#478/#481–#483). This doc describes the **as-built** code; the [History](#history) section at the end records what changed and why, so the rationale that `docs/` is meant to hold isn't lost. The companion plan doc [`docs/planning/legolas-wizard/spec.md`](planning/legolas-wizard/spec.md) is historical scratch — superseded by this doc; read it only for archaeology.

## What Legolas does

Project Gorgon's survey/treasure items (gated on **Geology**, **Mining**, or **Treasure Cartography** — "Surveying" is loose shorthand) go in the player's inventory. Using one prints a chat line like `[Status] The Iron Vein is 50m east and 30m north` and emits a `Player.log` `LocalPlayer: ProcessMapFx((X,Y,Z), …)` carrying the target's **exact world coordinate**. The item is consumed (and grants XP) only when used while *standing on* the target spot.

Legolas tails Player.log (only — post-#606 the chat-log subscription
retired alongside Phase 3 of the world-sim migration) and:

1. **Calibrates the area once** (guided, using the player's own in-game map pins) to learn the world↔overlay-pixel mapping. Persisted per area.
2. **Auto-places** every survey/treasure target at its true overlay position the moment `ProcessMapFx` arrives — placement is **absolute**; the user never clicks to confirm.
3. Draws a non-interactive "you are here" marker from the GameState position tracker (sparse: zone-in/teleport only).
4. Optimises a route through the unvisited pins.
5. Watches for `ProcessScreenText(ImportantInfo, "<Mineral> collected!")` lines to mark pins done; auto-resets when the last one drops (configurable).

The overlay is a HUD layered over the in-game map — strictly 1:1 with it, no internal zoom/pan ([#126](https://github.com/moumantai-gg/mithril/pull/127)).

> **The relative-offset / manual-anchor model is gone for Survey.** There is no anchor click, no per-ping click-to-confirm, no `CoordinateProjector.Refit`, no `IsAnchorEditable`. Targets are absolute world identities keyed off the calibrated area; player movement no longer invalidates anything. `CoordinateProjector` / `SessionState.PlayerPosition` survive but are **vestigial** (Survey never used them; post-[#488](https://github.com/moumantai-gg/mithril/issues/488) Motherlode doesn't either). If you are about to "fix" projection drift or re-add an anchor click for Survey, stop and read [History](#history) first.

## End-to-end Survey flow

```
Player.log tail         [Mithril.Shared — ILogStreamDriver, LocalPlayer pipe]   ProcessMapFx → absolute world coord; ProcessScreenText collect banners
IInventoryView.Bus      [Mithril.GameState — view-layer composer over PlayerWorld + ChatWorld]   InventoryItemAdded / InventoryStackChanged (post-#602)
   │
   ▼
PlayerLogParser   [Services/]   regex → MapTargetDetected | ItemCollected | MotherlodeDistance | MotherlodeUseDetected
   │
   ▼
PlayerLogIngestionService / ItemCollectionTracker   [Services/]   absolute placement, Tier-1 Add↔Collect correlator, surface inventory
   │
   ▼
SurveyFlowController    [Flow/SurveyFlowController.cs]   3-state FSM (+ optional SettingPosition); diagnoses out-of-state arrivals
   │
   ▼
SessionState            [ViewModels/SessionState.cs]   observable shared state (Surveys, SurveyPlayerPixel, …)
   │                       ▲
   │                       │ user input: drag/nudge a pin to correct it
   ▼                    MapOverlayViewModel             [ViewModels/MapOverlayViewModel.cs]
AreaCalibration          [Mithril.MapCalibration]      WorldToWindow: world coord → overlay pixel (per-area, persisted; #836 lift)
   │
   ▼
PinScene + renderer     [Rendering/]   immutable per-frame snapshot drawn via Direct2D in a D3DImage
   │
   ▼
MapOverlayView (XAML)   [Views/MapOverlayView.xaml]   thin WPF chrome + one D2DOverlaySurface for pins/routes/wedges/markers
```

The wizard ([`LegolasWizardViewModel`](../src/Legolas.Module/ViewModels/LegolasWizardViewModel.cs)) is the user-facing surface — it projects mode + FSM + calibration state onto a single `CurrentStep`. The user's only inputs to the loop are: the **guided calibration walkthrough** (once per area), dragging/nudging a mis-placed pin, **Optimize Route**, and the optional **Set my position** override. Survey placement itself is automatic.

## Coordinate model

### The raw data

`Player.log` `ProcessMapFx((X,Y,Z), …)` carries the survey/treasure target's **absolute world coordinate** in the area's engine-unit frame (verified: `Check Survey` 4/4 in a live log; Treasure Cartography shares the item template). This is the placement source of truth. The `[Status] The <name> is <a>m <dir> and <b>m <dir>` directional banner is now extracted from the **trailing string argument of the same `ProcessMapFx` line** (post-#606 — the chat-side parser retired) by [`PlayerLogParser.TryParseMapFxRelativeOffset`](../src/Legolas.Module/Services/PlayerLogParser.cs), and feeds the calibration verify-mode `NoteSurvey` hook. Naming / positioning both come from one Player.log line.

`MetreOffset(East, North)` and `CoordinateProjector` still exist but are **vestigial** — Survey doesn't touch them, and after [#488](https://github.com/moumantai-gg/mithril/issues/488) Motherlode solves in world space and no longer uses them either (see [History](#history)).

### Area calibration: world → pixel

The Survey placement transform is [`AreaCalibration`](../src/Mithril.MapCalibration/AreaCalibration.cs) (`WorldToWindow(WorldCoord, currentZoom) → PixelPoint`), a per-area 2D similarity transform lifted to shared infra in #836 and now persisted by [`IMapCalibrationService`](../src/Mithril.MapCalibration/IMapCalibrationService.cs) (which Legolas's [`IAreaCalibrationService`](../src/Legolas.Module/Services/AreaCalibrationService.cs) delegates to for reads + dual-writes during the transition window). It is fitted by [`LandmarkCalibrationSolver`](../src/Mithril.MapCalibration/LandmarkCalibrationSolver.cs) from *(world coordinate ↔ overlay pixel)* pairs.

The solver is a closed-form 2D similarity LSQ (origin X/Y, uniform scale, rotation), Umeyama-1991 specialised to 2D, expressed via 2D-complex arithmetic:

```
z_i = wx_i − j·wz_i           (world coord; − to flip world-N to screen-up)
w_i = px_i + j·py_i           (overlay pixel)
c   = Σ (w_i − w̄)·conj(z_i − z̄) / Σ |z_i − z̄|²
scale = |c|;  rotation = arg(c);  origin = w̄ − c·z̄
```

Needs ≥3 non-degenerate pairs; coincident-points cases no-op cleanly. Per-area, **persisted** to `%LocalAppData%/Mithril/Legolas/`.

### Bearing convention

The map uses `atan2(East, North)` (not `atan2(N, E)`) for offset bearings, paired with `atan2(dx, -dy)` for pixel bearings — both measured **clockwise from "up"** (map-north and screen-negative-Y). This matches in-game compass usage. WPF screen Y grows down; map north grows up — the north component is negated before adding to `origin.Y`. If you find yourself writing `atan2(N, E)` or `atan2(dy, dx)`, you're producing math-textbook bearings, not screen/map bearings.

## SurveyFlowController FSM

[`SurveyFlowController`](../src/Legolas.Module/Flow/SurveyFlowController.cs). Three states (absolute placement means no anchor bootstrap), plus one optional detour. Initial state is `Listening`.

| State | Meaning |
|---|---|
| `Listening` | Default working state. Absolute pins auto-place as `ProcessMapFx` arrives. The first pin of a cycle stamps `SessionState.StartedAt`. |
| `Gathering` | Route optimised; user is walking it. New targets are still **accepted**. |
| `Done` | All surveys collected. If `AutoResetWhenAllCollected`, `Reset()` immediately follows. |
| `SettingPosition` | **Optional.** Manual player-GPS override active: the overlay awaits a "click where you are" and the wizard shows a Cancel affordance. Entered on demand from `Listening`/`Gathering`; returns to whichever it came from (`ReturnState`). The auto tracker GPS is the zero-click default — this state only exists to *correct a stale anchor*. |

### Transitions

| From | Trigger | To | Notes |
|---|---|---|---|
| `Listening` | `Surveys.Add` (first pin, 0→1) | `Listening` | No transition — **stamps `SessionState.StartedAt`**. Re-stamps each cycle since `Reset` returns to `Listening`. |
| `Listening` | `OptimizeRoute()` | `Gathering` | `CanOptimize` = `Listening && Surveys.Count > 0`. |
| `Listening` \| `Gathering` | `AllCollected` (auto) | `Done` | Fires when the last uncollected survey is marked. |
| `Listening` \| `Gathering` | `RequestSetPosition()` | `SettingPosition` | Parks current state in `ReturnState`. Auto anchor untouched until the click confirms. |
| `SettingPosition` | `ConfirmPosition()` (map click) | `ReturnState` | `MapOverlayViewModel` writes the manual pixel (`SurveyPlayerIsManual = true`) *before* this call; the FSM only owns the phase. |
| `SettingPosition` | `CancelSetPosition()` | `ReturnState` | Anchor unchanged. |
| `Done` | `Reset()` (auto, if `AutoResetWhenAllCollected`) | `Listening` | Clears `Surveys` + `StartedAt`. |
| any | `Reset()` | `Listening` | Always — no anchor precondition. Clears `Surveys` + `StartedAt`; also exits `SettingPosition`. |

There is no `NoteSurveyDetected`/`CanAcceptSurvey`/drop path: absolute pins are added straight to `SessionState.Surveys` and the controller reacts via `OnSurveysChanged`. The cold-start "uncalibrated area" case is a **wizard step** (`WizardStep.Calibrating`), not an FSM state — `RecomputeStep` parks the wizard there until `IAreaCalibrationService.IsCurrentAreaCalibrated`. The `SettingPosition` detour is not its own `WizardStep`; `RecomputeStep` maps it back to its `ReturnState`'s step so the panel stays put while the overlay collects the click.

## Pin calibration: the guided two-phase walkthrough

> **Read this before "fixing" the calibration UI.** The label-agnostic rule and the named-pin prompt look contradictory and are not.

Cold-start calibration pairs *(world coordinate ↔ overlay pixel)* points and feeds them to `LandmarkCalibrationSolver` via `IAreaCalibrationService.CalibrateCurrentArea`. The world coordinates come from the player's own map pins. It is a guided, two-phase, correctable walkthrough driven by [`PinCalibrationCoordinator`](../src/Legolas.Module/Services/PinCalibrationCoordinator.cs), entered via the wizard's `Calibrating` step (the standalone `CalibrationSessionViewModel` landmark-route window is a separate, unchanged alternative).

**Pin source (#468).** The pin set is owned by the GameState-tier `IPlayerPinTracker` (`Mithril.GameState.Pins`), *not* Legolas. It parses `ProcessMapPin{Add,Remove}` (the only two verbs PG has — rename/move is Remove+Add; no clear/edit verb), is **area-scoped** (keyed off the shared `PlayerAreaTracker`), and **owns the login/area-entry replay** (PG bulk-re-emits every pin as an `Add` burst on entry → idempotent upsert keyed by rounded coordinate). Legolas's `PlayerLogParser` keeps only `ProcessMapFx` (survey targets are Legolas-owned, not shared pins).

### The two phases

A transparent overlay's click-through is **all-or-nothing** — it passes a right-click to the game *or* captures the user's left-click, never both. So calibration is two **explicit, user-toggled** phases (`CalibrationPhase`), never an automatic FSM edge. The phase trigger lives on the **wizard panel** (a normal, always-clickable window) plus optional bindable hotkeys (`legolas.calibration.phase.toggle` / `legolas.calibration.confirm`) — *not* on the transparent overlay.

- **Drop.** Overlay click-through ON. The user right-clicks the in-game map to place ≥3 well-spread pins (or relies on existing ones); Legolas only *observes* the live count (`PinsAvailable`). Entry starts here only when <3 usable pins exist.
- **Pair.** Overlay captures clicks. The coordinator names **one pin at a time** (`SuggestedPin`) by its in-game identity (`MapPin.DisplayName` + `Appearance`, e.g. *"red dot — 'Fire Magic 25'"*), chosen for **spread** (farthest-point from already-paired). The user left-clicks that pin's game-rendered dot through the overlay. **Advance is implicit** — pairing the next named pin *is* the advance; no per-pin confirm key. A pin can be **skipped** (deferred) or **overridden** (`OverridePin`). Entry starts here when ≥3 usable pins already exist (common case).

**Correction.** Placed pairs are `CalibrationMarker` VMs, so a marker can be selected (`TrySelectMarkerAt`), dragged (`DragSelectedTo`), or arrow-nudged (`NudgeSelected`) — the default nudge target is the just-placed marker. Correction edits **only the pixel half**; the tracker-supplied world coord is never mutated.

**Marker appearance (#478).** In-flow markers are styleable, **per-family** (one style for all in-flow markers), via `LegolasSettings.CalibrationPinStyle` — a `LegolasPinStyle` whose `Outer` is the selection ring (drawn only while `IsSelected`) and `Center` the always-on dot. Rendered through the **same converter/brush infra as the survey pins** (`PinShapeToGeometryConverter`, the stroke converters, `LegolasBrushes`' `Calibration*` brushes), so live settings edits repaint without restart. `CalibrationDefaults()` reproduces the pre-#478 hardcoded look (the v3→v4 schema bump is a visual no-op). The standalone `CalibrationOverlayView` window's markers were out of scope and are unchanged.

**Live, non-persisting residual.** Once ≥3 pairs exist, every add/nudge/drag re-runs the pure `LandmarkCalibrationSolver` *in-process* (`PreviewResidual`) — no persist, no `Changed`. Only the terminal **Confirm** / **Finish anyway** calls the persisting `CalibrateCurrentArea`. Confirm is gated on `≥3 pairs && residual ≤ LegolasSettings.CalibrationGoodResidualPx` (12 px default); "Finish anyway" persists despite a high residual so the user is never trapped behind the residual gate (the old "±10% non-affine map ceiling" framing was disproven — the renderer is exact; a high residual now means a bad fit / wrong zoom, not an inherent warp). "Finish anyway" is intentionally **panel-only** (no hotkey mirror) so a stray keypress can't persist a loose fit.

**Gesture/phase table.** Right-click = in-game pin drop (Drop; observed, never captured). Left-click on overlay = pair the named pin / grab a marker to drag (Pair; overlay captures). Arrow keys = nudge the selected/just-placed marker. Wizard-panel button (+ optional hotkey) = phase toggle / terminal Confirm. The view drives the overlay's click-through from the phase (`IsCalibrationDropping` ⇒ ON, `IsCalibrationCapturing` ⇒ OFF), overriding the user's `ClickThroughMap` preference for the duration.

**Why this does not violate the "never pair by name" rule.** The rule's intent is *no automatic name→point pairing*. It holds because the **solve is purely `(WorldCoord ↔ PixelPoint)`** — name/colour/shape never reach `LandmarkCalibrationSolver`; identity is used **only to help the human** decide which service-supplied world point they are deliberately clicking. The pairing is always a human click against a named target, never an inferred name→point map.

### In-flow recalibration

`IAreaCalibrationService.ClearCurrentAreaCalibration()` removes + persists the deletion and fires `Changed`. The Listening step offers a **"Recalibrate this area"** affordance (only when `CanRecalibrate` — area already calibrated) behind a **confirm guard** (`IsConfirmingRecalibrate`) so a misclick can't wipe a good calibration. Confirming clears the calibration → `Changed` → `RecomputeStep` routes back into `WizardStep.Calibrating` via the *same pin route as cold start* (`OnCurrentStepChanged` re-arms `PinCalibration`). No new top-level FSM state — existing edges reused. The standalone window's Recalibrate (landmark route) is the alternative.

### Validate calibration (#494, reworked #495)

> **`AreaCalibration.ResidualPixels` is *in-sample fit tightness*, not accuracy.** It measures how consistently you clicked the *same* pins it was fitted on (a 2-pin fit is ~0 by construction). A genuine check needs *independent* references.

A **"Validate calibration"** toggle sits in the wizard header **beside the calibration-indicator chip** (`WizardView.xaml`, declared after the chip so it docks to its left) — always-visible, not buried in a flow panel. It binds `MapOverlayViewModel.ToggleCalibrationValidationCommand` (command itself gated on `IsCurrentAreaCalibrated`); the button is hidden when uncalibrated and `IsEnabled`-bound to `LegolasWizardViewModel.CanValidateCalibration` — **available only when not surveying or plotting motherlodes** (the `CurrentStep` is the signal: not `Listening`/`Gathering`/`Motherlode{Measuring,Locating,Walk}`, and `CurrentStep` is `PickMode` before a mode is chosen, unlike the raw survey FSM which defaults to `Listening`). Entering such a step calls `MapOverlay.ForceHideCalibrationValidation()` from `OnCurrentStepChanged`, so an active overlay self-clears rather than cluttering the working flow.

On, it projects every `IAreaCalibrationService.CurrentAreaReferences` entry (area landmarks + NPCs) through the persisted `AreaCalibration.ProjectWorld` into `CalibrationGhosts` via `GhostLabelDeclutter.Build` — each a magenta `GhostMarker(Name, Pixel, ShowLabel)`. Rendered by a **WPF `ItemsControl` layered over the D2D surface** (the `CalibrationMarkers` idiom in `MapOverlayView.xaml`: zero-size `Canvas`, dot via negative offsets, `Border`+`TextBlock` label at `+9,-9`) — WPF draws above the D2D surface so the "never occluded by a real pin" property holds, and each marker now carries a **readable label**. `ShowLabel` is the greedy declutter decision: the dot always draws; a label is suppressed when its estimated box would overlap an already-placed one. Input order is priority — the area service emits **NPCs before landmarks**, so NPC labels win a crowding contest for free (no second sort). The user eyeballs whether each marker sits on its real map feature. **Deliberately no zoom compensation** — a consistent offset is the diagnostic ("recalibrate; usually a map-zoom change"), matching the absolute `ProcessMapFx` pin path. Markers refresh on `IAreaCalibrationService.Changed` (area switch / recalibrate; calibration lost ⇒ overlay self-drops). The status line (residual *relabelled* as fit tightness) is shown as an **on-overlay magenta banner** while validation is active (it left the wizard panel with the button). Turning validation on captures the overlay's prior user-intended visibility and forces it up; turning off (toggle, force-hide, or calibration-lost) **restores that visibility** instead of leaving the overlay stuck open. The standalone `CalibrationOverlayView`'s `ProjectLandmarks` (unplaced-only) is the older, separate equivalent — left unchanged.

## SessionState

[`SessionState`](../src/Legolas.Module/ViewModels/SessionState.cs) is the shared observable model. Fields that matter for Survey:

| Field | Lifetime | Notes |
|---|---|---|
| `Surveys : ObservableCollection<SurveyItemViewModel>` | Session | Cleared by `Reset()`. Fires `AllCollected` when last uncollected is marked. |
| `SurveyPlayerPixel : PixelPoint?` | Session | **Survey "you are here".** The `IPlayerPositionTracker` world fix projected through the current area's calibration. Null until a fix lands in a calibrated area. Route start + rendered marker + pre-first-collection segment. Set by `MapOverlayViewModel`. |
| `SurveyPlayerMeasuredAt : DateTimeOffset?`, `SurveyPlayerSource : PlayerPositionSource?` | Session | Staleness of `SurveyPlayerPixel`. Surfaced via `MapOverlayViewModel.PlayerAnchorStatus` — never drawn as live. (`Source` is null for a manual override.) |
| `SurveyPlayerIsManual : bool` | Session | True when `SurveyPlayerPixel` came from the user's "Set my position" click (the `SettingPosition` detour) **or** a #497 character-named pin. Calibration-independent for the click; survives a calibration re-apply; superseded by the next *fresh* tracker fix. `PlayerAnchorStatus` shows `"You — set manually"`. |
| `SurveyPlayerIsPinned : bool` | Session | #497: the manual anchor is a character-named / `@me` map pin (implies `IsManual`). `PlayerAnchorStatus` shows `"You — pinned, …"`. When the pin stops winning, both flags clear so auto resumes; a genuine pixel-click manual (`IsManual && !IsPinned`) keeps its #476 stickiness. Precedence lives in the pure `MapOverlayViewModel.ResolveSurveyAnchor`. |
| `PlayerPosition : PixelPoint` / `HasPlayerPosition : bool` | Session | **Vestigial post-[#488](https://github.com/moumantai-gg/mithril/issues/488).** Was the manual map-click anchor the old Motherlode triangulation recorded; the rebuilt mechanic is log-driven world-space multilateration and never reads it. Survey never read it. Retained only because the `SetPlayerPosition` hotkey + overlay click still mutate it. |
| `SelectedSurvey : SurveyItemViewModel?` | UI | Drives nudge-command targeting. Auto-set to the most-recently-placed survey by `PlayerLogIngestionService`. |
| `IsMapVisible`, `IsInventoryVisible`, `IsCalibrationVisible : bool` | UI | Overlay visibility intent — `OverlayController` reacts. |
| `MapOpacity`, `InventoryOpacity : double` | **Persisted** | Bidirectionally synced with `LegolasSettings` in `LegolasModule.Register`. |
| `Mode : SessionMode` | Session | `Survey \| Motherlode`. |

## Settings & persistence

[`LegolasSettings`](../src/Legolas.Module/Domain/LegolasSettings.cs) is global (no per-character override). Highlights:

| Property | Default | Notes |
|---|---|---|
| `SurveyDedupRadiusMetres` | 5.0 | A new target within this radius of an uncollected pin updates that pin instead of creating a new one. |
| `SurveyPinRadiusMetres` | 8.0 | Pin diameter in **screen pixels** (misnamed — see Pitfalls). |
| `CalibrationGoodResidualPx` | 12.0 | Confirm gate for the guided calibration; "Finish anyway" bypasses it. |
| `CalibrationPinStyle` | `CalibrationDefaults()` | Per-family in-flow calibration marker style (#478). |
| `MapOpacity`, `InventoryOpacity` | 1.0 | Floored at `MinInteractiveOpacity = 0.01` so a faded overlay stays clickable ([#124](https://github.com/moumantai-gg/mithril/pull/124)). |
| `ClickThroughMap`, `ClickThroughInventory` | false | Toggles `WS_EX_TRANSPARENT`. Overridden during a calibration phase. |
| `AutoClickThroughInventoryDuringSession` | true | Auto-engage inventory click-through while a survey is active. |
| `AutoHideOverlaysOnGameUnfocused` | true | |
| `HideOverlaysBetweenSessions` | true | Hide both overlays on `→ Done`; re-show on the next cycle's first pin. |
| `AutoResetWhenAllCollected` | true | After `Done`, controller calls `Reset()`. |
| `ShowBearingWedges` | true | Uncertainty arcs for uncorrected pins. |
| `NudgeStepDefault \| Fast \| Fine` | 1.0, 5.0, 0.25 | Pixel magnitudes; read fresh on every command execute. |
| `PerfHarnessPinCount` | 30 | Synthetic-load pin count (clamped 1–1000). |
| `MapOverlay`, `InventoryOverlay : WindowLayout` | — | Position + size, bound via `WindowLayoutBinder`. |

JSON persistence is an AOT source-generated context ([`LegolasSettingsJsonContext`](../src/Legolas.Module/Domain/LegolasSettingsJsonContext.cs)), camelCase, pretty-printed, schema-versioned (currently v4 — #478 visual no-op bump). Per-area calibration is persisted separately by `IAreaCalibrationService`. Auto-save is `SettingsAutoSaver<LegolasSettings>` (debounced `PropertyChanged`, synchronous shutdown flush); `WindowLayoutBinder.Bind(window, layout, saver.Touch)` is the explicit dirty signal for layout mutations (the layout object is a sibling, so it doesn't fire `LegolasSettings.PropertyChanged`).

**Per-game-session, not persisted:** the survey list and run state.

## Hotkeys & click-through

All `IHotkeyCommand` implementations live in [`Hotkeys/Commands.cs`](../src/Legolas.Module/Hotkeys/Commands.cs) and are registered in `LegolasModule.Register`. **None ship with a default binding** — arrow keys collide with in-game movement, so the user opts into specific bindings via `Settings → Hotkeys`.

| Category | Commands |
|---|---|
| Session | `legolas.session.start`, `legolas.session.mark_collected`, `legolas.route.optimize` |
| Mode | `legolas.mode.survey`, `legolas.mode.motherlode` |
| Overlay | `legolas.overlay.{map,inventory,all,calibration}.toggle`, `legolas.overlay.{map,inventory}.clickthrough.toggle`, `legolas.overlay.wedges.toggle` |
| Pin Nudge | `legolas.pin.nudge.{up,down,left,right}` × `{,.fast,.fine}` = 12 |
| Calibration | `legolas.calibration.phase.toggle`, `legolas.calibration.confirm` (gated on `PinCalibration.IsArmed`; `confirm` also on `CanConfirm && IsResidualGood`) |
| Diagnostics (dev-only) | `legolas.diag.frame_logger.toggle`, `legolas.diag.perf_harness.run`, `legolas.diag.perf_harness.sweep` (`IsDeveloperOnly`) |

> **Note:** there is no `legolas.session.set_position` command — the manual-position override (#476 Option C) is a wizard-driven detour ("Set my position"), not a top-level hotkey. The Calibration hotkeys are optional bindable mirrors of the wizard-panel buttons, gated so the keys aren't eaten system-wide outside an armed walkthrough.

### Pin-nudge target precedence

`NudgePinCommandBase.ExecuteAsync` and the on-screen nudge pad both call the **single** `MapOverlayViewModel.Nudge(dx, dy, step)` so keyboard and pad can't diverge. Precedence:

1. A selected **calibration marker** (the guided walkthrough's just-placed/selected marker) → `PinCalibrationCoordinator.NudgeSelected`.
2. The selected `SessionState.SelectedSurvey` pin → `CorrectSurveyCommand` (a survey always wins over the manual anchor).
3. The **manual** Survey player anchor — only when no survey is selected and `SurveyPlayerIsManual`: mutate `SurveyPlayerPixel` only, keep the manual flag (a fresh tracker fix still supersedes per #476), never touch the legacy `PlayerPosition`.
4. Else → no-op.

The auto/tracker-projected anchor is intentionally **non-interactive** — nudging a data-sourced fix would mask staleness. `NudgePinCommandBase.IsRegistrable` and `NudgePadViewModel.IsAvailable` track all of (1)–(3) so arrow keys aren't eaten system-wide when there's nothing to nudge ([#139](https://github.com/moumantai-gg/mithril/issues/139)).

### Click-through

[`Controls/ClickThrough.cs`](../src/Legolas.Module/Controls/ClickThrough.cs) wraps `GetWindowLong`/`SetWindowLong` to flip `WS_EX_TRANSPARENT | WS_EX_LAYERED`. Applied on window load and whenever `ClickThroughMap`/`ClickThroughInventory` changes. `ForceTopmost` is called on activate so a click-through overlay can't fall behind the game. [`Services/AutoOverlayCoordinator.cs`](../src/Legolas.Module/Services/AutoOverlayCoordinator.cs) auto-engages `ClickThroughInventory` while a survey is active. During a calibration phase the view drives the map overlay's click-through from the phase, overriding the user preference for the duration.

## Diagnostics

A frame-time logger plus a synthetic load harness ship with the module so renderer/FSM changes can be measured against the same fixture.

| Component | Purpose |
|---|---|
| [`Diagnostics/FrameTimeLogger.cs`](../src/Legolas.Module/Diagnostics/FrameTimeLogger.cs) | Hooks `CompositionTarget.Rendering`, samples wall-clock dt, writes a CSV (one row/frame) + a `.txt` summary (mean / p50 / p95 / p99 / max / stutter count > 33 ms) + a config snapshot. Singleton; `Start`/`Stop` reentrant. |
| [`Diagnostics/SurveyPerfHarness.cs`](../src/Legolas.Module/Diagnostics/SurveyPerfHarness.cs) | Drives synthetic load through the live overlay: reset, anchor at map centre, inject deterministic surveys, capture Listening (with `SelectedSurvey` set) 15 s, `OptimizeRoute` → Gathering 15 s. `RunTreatmentSweepAsync` iterates Halo → Glow for matched A/B reports. |

Three hotkey commands under **Legolas · Diagnostics** drive this; all gated under `ShellSettings.DeveloperMode` via `IHotkeyCommand.IsDeveloperOnly`, no default bindings. Pin count from `LegolasSettings.PerfHarnessPinCount`. Reports land in `%LocalAppData%/Mithril/Legolas/perf/`, each CSV/`.txt` pair carrying the active config (treatment, pin count, transparency, FSM state, window size).

### Acceptance criteria — perf floor

The D2D renderer was scoped against this 100-pin-with-game baseline. Future renderer changes shouldn't fall below it without explicit discussion:

| Metric | Floor |
|---|---|
| `fps_mean` | ≥ 110 |
| `dt_ms_p99` | < 18 ms |
| `stutter_>33ms` per 15 s phase | < 1 |

Pre-rewrite WPF baseline was 67–84 fps / p99 27–30 ms / 2–4 stutters for the same fixture; the renderer comfortably clears the floor on all four (Halo / Glow) × (Listening / Gathering) cells.

## Key files

| File | What's in it |
|---|---|
| [`LegolasModule.cs`](../src/Legolas.Module/LegolasModule.cs) | DI registration, hosted services, hotkey command list. Module entry point. |
| [`ViewModels/LegolasWizardViewModel.cs`](../src/Legolas.Module/ViewModels/LegolasWizardViewModel.cs) | The user-facing wizard. Projects mode + FSM + calibration state onto `CurrentStep`. |
| [`Flow/SurveyFlowController.cs`](../src/Legolas.Module/Flow/SurveyFlowController.cs) | Survey FSM (3 states + `SettingPosition`). |
| [`Flow/MotherlodeFlowController.cs`](../src/Legolas.Module/Flow/MotherlodeFlowController.cs) | Motherlode-mode FSM (out of scope here). |
| [`Services/PlayerLogParser.cs`](../src/Legolas.Module/Services/PlayerLogParser.cs) | `Player.log` parsing — `ProcessMapFx` (absolute survey targets + inline relative-offset for calibration verify), `ProcessScreenText` (`<Mineral> collected!` + motherlode distance), `ProcessDoDelayLoop` (motherlode use gesture). |
| [`Services/PlayerLogIngestionService.cs`](../src/Legolas.Module/Services/PlayerLogIngestionService.cs) | `BackgroundService`. Area→calibration bridge, absolute pin placement, motherlode coordinator wiring. |
| [`Services/ItemCollectionTracker.cs`](../src/Legolas.Module/Services/ItemCollectionTracker.cs) | `BackgroundService`. Tier-1 Add↔Collect correlator (#606 replacement for the retired chat-tail `LogIngestionService`); subscribes to `IInventoryView.Bus` + parses `ProcessScreenText` collect banners. |
| [`Services/AreaCalibrationService.cs`](../src/Legolas.Module/Services/AreaCalibrationService.cs) | Calibration walkthrough lifecycle + `Changed`. Persistence lifted to [`IMapCalibrationService`](../src/Mithril.MapCalibration/IMapCalibrationService.cs) (#836); this service delegates reads + dual-writes during the transition window. |
| [`Services/PinCalibrationCoordinator.cs`](../src/Legolas.Module/Services/PinCalibrationCoordinator.cs) | The guided two-phase calibration walkthrough (Drop/Pair, correction, residual, Confirm). |
| [`Mithril.MapCalibration/LandmarkCalibrationSolver.cs`](../src/Mithril.MapCalibration/LandmarkCalibrationSolver.cs) | Pure 2D similarity LSQ solver (lifted out of Legolas in #836). |
| [`Mithril.MapCalibration/AreaCalibration.cs`](../src/Mithril.MapCalibration/AreaCalibration.cs) | The world↔pixel transform (`WorldToWindow` + `WindowToWorld`; lifted in #836). |
| [`Services/CoordinateProjector.cs`](../src/Legolas.Module/Services/CoordinateProjector.cs) | Metre→pixel projection. **Vestigial** post-[#488](https://github.com/moumantai-gg/mithril/issues/488) (neither Survey nor the rebuilt Motherlode uses it). |
| [`ViewModels/SessionState.cs`](../src/Legolas.Module/ViewModels/SessionState.cs) | Shared observable state. |
| [`ViewModels/MapOverlayViewModel.cs`](../src/Legolas.Module/ViewModels/MapOverlayViewModel.cs) | Click handling, pin placement, route + wedge geometry, nudge dispatch. |
| [`Views/MapOverlayView.xaml{,.cs}`](../src/Legolas.Module/Views/MapOverlayView.xaml) | Overlay UI: WPF chrome + a single D2D surface. Hosts drag/click handlers + the calibration marker DataTemplate. |
| [`Rendering/D2DOverlaySurface.cs`](../src/Legolas.Module/Rendering/D2DOverlaySurface.cs) | `FrameworkElement` wrapping `D3DImage`; render loop, per-monitor DPI, `Stretch.Fill` (see Pitfalls). |
| [`Rendering/D3DDeviceLifecycle.cs`](../src/Legolas.Module/Rendering/D3DDeviceLifecycle.cs) | D3D11 + D3D9Ex device pair, shared-handle texture, D2D render target. The "shared-surface dance". |
| [`Rendering/PinScene.cs`](../src/Legolas.Module/Rendering/PinScene.cs) / [`PinSceneRenderer.cs`](../src/Legolas.Module/Rendering/PinSceneRenderer.cs) | Immutable per-frame snapshot + pure Direct2D draw logic. |
| [`Hotkeys/Commands.cs`](../src/Legolas.Module/Hotkeys/Commands.cs) | All hotkey commands. |
| [`Controls/ClickThrough.cs`](../src/Legolas.Module/Controls/ClickThrough.cs) | `WS_EX_TRANSPARENT` P/Invoke helpers. |
| [`Domain/LegolasSettings.cs`](../src/Legolas.Module/Domain/LegolasSettings.cs) | Persisted settings. |

## Constraints & pitfalls

A working list of "things that have bitten contributors". Read before changing placement / FSM / renderer logic.

### Player position is sparse; the "you are here" marker goes stale by design

Target *pins* are absolute and exact. The Survey **player marker** (`SurveyPlayerPixel`) comes from `IPlayerPositionTracker`, whose fixes are sparse — `ProcessAddPlayer`/`ProcessNewPosition` = zone-in / teleport only. There is genuinely **no per-tick footstep feed in Player.log**. The marker is accurate at zone-in and goes stale as the player walks — *by design, not a bug to "fix"*. Do not relax the staleness surfacing (`PlayerAnchorStatus` / `MeasuredAt` / `Source`); never present the marker as live. The user's recourse when it's stale is the manual override (`SurveyFlowState.SettingPosition`) — that is the designed answer, not making the auto signal pretend to be denser than it is.

### Don't re-introduce the relative/anchor model for Survey

`IsAnchorEditable`, the editable player marker, `CoordinateProjector.Refit`, the anchor click, and the per-ping click-to-confirm loop were **deliberately removed** (placement is absolute). `PlayerPosition`/`CoordinateProjector` are vestigial — Survey never used them and post-[#488](https://github.com/moumantai-gg/mithril/issues/488) Motherlode doesn't either. The #476 manual anchor is *not* the old model — it is a raw screen pixel on the shared marker layer, superseded by the next fresh tracker fix. If a "projection drift" bug tempts you toward an anchor click, the real lever is **area calibration quality**, not a per-session anchor.

### Calibration solves on `(WorldCoord ↔ PixelPoint)` only — never by name

`LandmarkCalibrationSolver` must never see pin name/colour/shape. Identity is shown to the *human* to help them pick which point to click; it must not flow into the solve. See "Why this does not violate the rule" above before touching `PinCalibrationCoordinator`.

### Calibration is persisted per-area; an uncalibrated area places nothing

`RecomputeStep` parks the wizard on `Calibrating` until `IAreaCalibrationService.IsCurrentAreaCalibrated`. A regression that drops persistence, or an area-key mismatch, manifests as "the wizard won't leave Calibrate" or "every area asks again". `IAreaCalibrationService.Changed` is the signal that re-runs `RecomputeStep` — recalibration relies on it firing on clear.

### Pin radius is in pixels, not metres

`SurveyPinRadiusMetres` is misnamed — read as a pixel value. An earlier version multiplied by projector scale, which made pins visibly resize on every refit. The fix kept the name. Don't "fix" the units.

### Overlay is strictly 1:1 with the game map

No internal zoom/pan ([#126](https://github.com/moumantai-gg/mithril/pull/127)). Window size/position are user-controlled (`WindowLayoutBinder`); the D2D canvas renders at exactly 1 DIP per CSS pixel and the D3D11 back buffer is device-pixel-sized for per-monitor DPI. **`D2DOverlaySurface` must host the `D3DImage` with `Stretch.Fill`, not `Stretch.None`** ([#481](https://github.com/moumantai-gg/mithril/issues/481)): the back buffer is `(W·s)×(H·s)` device pixels but the `D3DImage` stays at 96 DPI; `None` would map one back-buffer pixel to one DIP and mis-scale the whole pin layer at display scaling ≠ 100% (pins drift proportional to distance from top-left). `Fill` composites the buffer 1:1 onto the `W×H` DIP box. Don't switch it back.

### Second-run `StartedAt` regression — stamp on the first pin in `Listening`

`SessionState.StartedAt` is stamped inside `SurveyFlowController.OnSurveysChanged` the moment the first pin of a cycle lands (0→1) while `Listening`. `AutoResetWhenAllCollected = true` returns to `Listening` and clears `StartedAt`, so the next cycle's first pin must re-stamp. Symptom if broken: the share-card shows `0s elapsed` despite a multi-minute run. Pinned by `SecondCycle_after_AutoReset_re_stamps_StartedAt` in `SurveyFlowControllerTests`.

### Renderer is Direct2D in a D3DImage, not WPF retained-mode

The pin/route/wedge/marker layer is drawn immediate-mode by `PinSceneRenderer` on a Direct2D render target presented through a `D3DImage`. WPF chrome is still vanilla XAML; the window keeps `AllowsTransparency="True"`. Hit-testing for pins is via `Viewport_MouseLeftButton*` handlers on the WPF Viewport (`D2DOverlaySurface` is `IsHitTestVisible=False`); selection comes from the wizard ListBox. Hover tooltips were not reproduced — the right answer if missed is a separate WPF popup driven by a virtual hit-test against the latest `PinScene`, not a re-introduction of WPF pins.

## History

Why the model looks the way it does. Newest first. The pre-rewrite model (relative offsets, a manually-clicked player anchor, `CoordinateProjector.Refit`, per-ping click-to-confirm) is preserved in git history; this section records the *why* of each step away from it.

- **#497** — **Declare position via a character-named (or `@me`) map pin.** `CharacterPinAnchor` (consumes `IPlayerPinTracker` + `IActiveCharacterService`) resolves a pin whose label matches the active character name or the `@me` sentinel to an exact world fix. Survey: a *manual* anchor sourced from the pin's exact world coord (projected via calibration), freshest-wins — beats the pixel click + a stale auto fix, sticky vs a calibration re-apply, superseded by a genuinely newer tracker fix. Precedence extracted into the pure, unit-tested `MapOverlayViewModel.ResolveSurveyAnchor` (no-pin path byte-identical → `SurveyPlayerGpsTests` unchanged as the regression guard). Motherlode: a self-pin is the *preferred* feeder #2 (`MotherlodePositionSource.NamedMapPin`, confidence 0.85, above generic `MapPin` 0.6). A deliberate **self-declaration** — not #454 name-*inference* (the user names their own marker; the rule forbids inferring *game-entity* pairings by name). Pure consumption of existing signals; no GameState/parser change. Pixel-click override kept as the uncalibrated fallback.
- **#113** — Motherlode resolver **made actionable** (post-#488 the panel showed only raw engine-unit `(X,Z)` + GDOP/residual jargon, useless in a game with no coordinate readout). Five layers: (1) `MotherlodeReferenceLocator` phrases each solved treasure relative to the nearest ranked reference — the player's own measured spots (`MotherlodeStatus.Locations`, now surfaced not just counted), their map pins (`IPlayerPinTracker`), the area landmark/NPC gazetteer (`IAreaCalibrationService.CurrentAreaReferences`); globally-nearest wins, tier only breaks exact ties, all calibration-free. (2) The solver's own `MultilaterationQuality` is carried onto `MotherlodeSurvey` (not re-derived) → a plain-language confidence pill; raw coord/GDOP/residual demoted to a row tooltip. (3) `RouteOrder`/`Collected`/next-up now actually rendered (Optimize/collection were previously invisible). (4) Derived `MotherlodeStage` (Measuring/Locating/Walk/Done) projected onto four wizard sub-steps + breadcrumb — `MotherlodeFlowController` stays coarse. (5) On-map marker: a new explicit Motherlode→`PinScene` feed (the same gap that orphaned the bearing wedge, [#492](https://github.com/moumantai-gg/mithril/issues/492)) — `PinScene.MotherlodePins` projected via `AreaCalibration.ProjectWorld`, **gated on a calibrated area** (degrades to no-dot, never a wrong dot) and surfaced as approximate (detection/zoom accuracy, not a renderer warp — the ±10% "non-affine" band was disproven) while the relative text stays exact. `CardinalDirection.FromBearing` is the inverse of `ToBearingRadians` (same `atan2(East,North)` clockwise-from-map-north convention). Manual live-session UI E2E still owed (with #488's).
- **#494** — Always-available **"Validate calibration"** toggle (Listening/Gathering): projects area landmarks/NPCs through the persisted `AreaCalibration.ProjectWorld` as hollow magenta ghost markers on the live D2D overlay so the user can eyeball alignment against the real map. Honest framing — `ResidualPixels` is in-sample fit tightness, not accuracy; the independent ghosts are the real check. No zoom compensation (a consistent offset *is* the "recalibrate" signal). Pure surfacing/reuse of `ProjectWorld` + `CurrentAreaReferences`; no new math. Standalone-window `ProjectLandmarks` left as-is.
- **#488** — Motherlode rebuilt as **range-only weighted-NLS multilateration** over a source-agnostic world-coord position contract. The shipped path never ran (manual distance, overlay-pixel position, a 3-circle solver mixing pixel centres with metre radii, capped at 3 samples). Now: distance from the ChatLog `The treasure is N meters from here` line (no DIR token = the discriminator), the use gesture from Player.log `ProcessDoDelayLoop("Using … Motherlode Map")`, position from a feeder — opportunistic `ProcessAddPlayer`/`ProcessNewPosition` (#1, zero standoff) or a `ProcessMapPinAdd` pin (#2). `MotherlodeMeasurementCoordinator` pairs them by **timestamp** (label-agnostic, `_liveSince`-replay-safe); `MultilaterationSolver` solves each treasure in world space (linear init → weighted Gauss–Newton/LM → RANSAC → GDOP gate); `MotherlodeFlowController` unchanged. **Consequence: `SessionState.PlayerPosition` / `CoordinateProjector` / `MetreOffset` are no longer used by Motherlode — now vestigial, not "Motherlode-only".** Max-accuracy field procedure (a player choice, not special-cased): log out and back in at each spot to force a feeder-#1 fix exactly where you stand. The bearing-wedge affordance the old triangulation needed is orphaned by this — removal tracked in [#492](https://github.com/moumantai-gg/mithril/issues/492).
- **#478** — In-flow calibration markers made user-configurable, per-family, reusing the survey-pin converter/brush infra. `LegolasSettings` v3→v4 (visual no-op via `CalibrationDefaults()`).
- **#477** — Calibration became a **guided, two-phase, correctable** walkthrough (`PinCalibrationCoordinator`): explicit Drop/Pair phases (the all-or-nothing click-through forces an explicit toggle, not an FSM edge), per-pin drag/nudge correction, live non-persisting residual. Part B added in-flow **recalibration** behind a confirm guard. Replaced the earlier "existing-pins route vs. freshly-dropped turn-order route" branch.
- **#481–#483** — Overlay rendering corrected at display scaling ≠ 100% (`Stretch.Fill`); overlay-topmost regression fixed.
- **#476** — The anchor click *was* (undesigned) also the Survey player-position reference (you-are-here marker, route start, first segment). #460 removed all three; #476 restored them from a better source — the GameState `IPlayerPositionTracker` world fix projected through area calibration, **not** a manual click — plus the optional manual-override detour (Option C / `SettingPosition`) for when the sparse signal is stale. Placement stayed absolute.
- **#468** — Player map-pins promoted to a shared GameState service (`IPlayerPinTracker`, `Mithril.GameState.Pins`); calibration consumes it instead of Legolas owning pin parsing.
- **#460** — Cold-start calibration moved to a wizard `Calibrating` step driven by the map overlay (pin pairing), persisted per area. The Survey FSM collapsed: `AwaitingPosition`/`Ready` removed; `Listening` is the resting state; the anchor click became the explicit, optional `SettingPosition` detour.
- **#454** — The pivot. `ProcessMapFx` in `Player.log` carries each target's **absolute world coordinate** (verified in a live log). Legolas began consuming `IPlayerLogStream`; targets became absolute identities keyed off the calibrated area. The relative-offset model, the anchor click, `Refit`, `IsAnchorEditable`, and the `Gathering` survey-drop mitigation were retired for Survey — player movement no longer invalidates placement. This is what made everything above possible.
- **#126/#127** — Internal overlay zoom/pan removed; the overlay became strictly 1:1 with the game map.
- **Pre-#454 (now history)** — The original model: `[Status]` lines parsed as relative offsets from a manually-clicked player anchor; ≥2 pin corrections triggered a 4-DOF similarity `Refit`; the FSM dropped new surveys in `Gathering` because movement was undetectable and would mis-place them. The whole "movement invalidates the projector" class of pitfalls belonged to this model and is gone.
