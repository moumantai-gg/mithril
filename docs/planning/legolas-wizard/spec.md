# Legolas wizard

**Tracked in:** #111 (PR1 — wizard, shipped). #112 (PR2 — dashboard restyle) closed as no-work-required during PR1; see "PR1 outcome" below. #113 (PR3 — Motherlode wizard depth) still open. Umbrella #4 is closed.

Follow-on to [legolas-state-machine.md](legolas-state-machine.md). The FSMs
(#109) and the cosmetic consolidation (#110) shipped. This plan covered
the remaining bullets of #4. The wizard is now the only Legolas view —
the planned dashboard was killed mid-PR1 (see PR1 outcome).

## PR1 outcome (what actually shipped)

The plan as originally drafted assumed two coexisting views — wizard and
dashboard — gated by a `LegolasUiMode` setting. During PR1 the dashboard
was killed; the rationale was that once the wizard exposed every action
(via the step bodies plus the header's Back/Reset icons), the dashboard
was just "more controls visible at once even when not actionable" — a
parallel view tree without clear payoff. The author's gut: "what would
a power-user view even do?" Killing it simplified the codebase and made
#112 unnecessary.

### Final shipped shape

- **Wizard is the only Legolas view.** `LegolasPanelView` is a thin
  shell hosting `WizardView` directly. No `LegolasUiMode` setting, no
  dashboard view tree.
- **`LegolasSettingsView` lives in the shell's per-module settings
  tab** (Decision 2 preserved). Hosts overlay options, click-through,
  marker colors, inventory grid.
- **Wizard layout**: module header (icon + accented title) at top,
  breadcrumb below (visible after mode pick), step hero row with the
  per-step title and inline nav (`[← Back]  Step Title  [↻ Reset]`),
  step body with primary action(s), status-strip footer.
- **Header nav semantics**:
  - **Back** is *step-wise*, not "always to PickMode" — see `Back()` in
    `LegolasWizardViewModel`. AwaitingPosition → PickMode. Listening →
    AwaitingPosition (clears anchor + surveys). AwaitingPin → Listening
    (cancels pending pin). Gathering / Done → full reset (route's
    position anchor is invalidated once walking begins). MotherlodeMeasuring
    → PickMode.
  - **Reset** is the wizard-level "start this flow from scratch" — for
    Survey it clears the anchor + surveys + pending and lands on
    AwaitingPosition unconditionally (more aggressive than
    `SurveyFlowController.Reset()` which preserves the anchor). For
    Motherlode it clears all positions/distances/slots.
- **Auto-show map overlay** when entering AwaitingPosition (so the user
  has something to click). Inventory overlay stays user-controlled.
- **Reset preserves overlay visibility**; only Change-mode (= Back from
  AwaitingPosition) hides them. "Reset = do this flow again",
  "Change mode = start fresh".
- **Listening's primary action is "Go!"** (renamed from "Optimize Route").
  Same for Motherlode's optimize action.
- **Buttons match the rest of the app** — `WizardPrimaryButton`
  inherits the implicit `Button` style via `BasedOn="{StaticResource
  {x:Type Button}}"` (omitting this BasedOn reverts to WPF's white-box
  default; not obvious if you don't know the WPF rule).

### What this means for the original Decisions

- **Decision 1 (UI mode default)** — moot, no UI mode toggle exists.
- **Decision 2 (settings to shell tab)** — preserved, shipped.
- **Decision 3 (mode pick = wizard step 0)** — preserved, shipped.
- **Decision 4 (Motherlode coarse for PR1)** — preserved; PR3 (#113)
  revisits.

The historical decision memo below is preserved as-is for future
context. Read with the understanding that the dashboard half no longer
applies.

---

## Original decision memo (historical, pre-dashboard-kill)

This document was a **decision memo, not an implementation prescription**.
Several UX calls (first-run default, where settings live, Motherlode
wizard depth) needed user sign-off before coding. Each open call was
flagged inline with **DECIDE:**.

## What we're starting from

| File | Today's role | After this work |
|---|---|---|
| `LegolasPanelView.xaml` | One scrolling stack: header → overlay toggle → mode radio → session controls → overlay options → click-through → colors → grid → Motherlode panel | Becomes a **shell** (`ContentControl` switching on `LegolasUiMode`) hosting either `WizardView` or `DashboardView`. Settings groups (overlay options, click-through, colors, grid) move to a `SettingsView` reachable from both. |
| `LegolasPanelViewModel` | Owns refs to every sub-VM | Unchanged shape, gains `UiMode` property bound to the new setting. |
| `SurveyFlowController.CurrentState` | Drives `PhaseDescription` + button enablement | Drives wizard's `DataTemplateSelector`. |
| `MotherlodeFlowController.CurrentState` | Three states; not granular enough for wizard steps | Stays — the wizard derives finer step IDs in a wizard-only VM (see **Motherlode wizard depth** below). |

## Scope of the FIRST wizard PR

Keep the first PR sized for one review pass:

1. New `LegolasUiMode` enum + setting; default `Wizard` for everyone.
2. `WizardView.xaml` covering **Survey mode end-to-end** (mode-pick
   step 0 + 5 templates, one per `SurveyFlowState`).
3. Settings extracted into a `LegolasSettingsView` wired to
   `IMithrilModule.SettingsViewType` (overlay options, click-through,
   colors, grid). The main panel keeps only session-relevant controls.
4. `DashboardView.xaml` = lift-and-shift of what's left of today's
   `LegolasPanelView` after settings are extracted. (No restyling yet.)
5. Motherlode in wizard mode falls through to the existing motherlode
   group rendered in a single template — no wizard steps for it in PR1.
6. "Use dashboard from now on" button inside the wizard footer (flips
   `UiMode = Dashboard` and `HasSeenWizard = true`).
7. Tests for the new wizard VM (state→step mapping, settings round-trip).

Dashboard polish (re-styling the lifted content into a tight status
strip + contextual action panel) is a separate PR2. Motherlode wizard
depth is PR3, deferred until after manual testing of PR1.

This split keeps each PR reviewable and lets the user try the Survey
wizard before we commit to the dashboard restyle.

## Decisions (locked in)

### Decision 1: First-run default for `LegolasUiMode`

**Wizard for everyone, including upgrades.** Existing users see the
wizard once after upgrade; the in-wizard "Use dashboard from now on"
button is the exit ramp. No migration code, one default, the wizard is
the front door.

### Decision 2: Where do settings live?

**Banished to the shell.** Settings move to a `LegolasSettingsView`
exposed through `IMithrilModule.SettingsViewType` — same slot Bilbo,
Pippin et al already use. Today's overlay-options / click-through /
marker-colors / inventory-grid groups all migrate; the main panel keeps
only the session-relevant controls (Start/Reset, Set Player Position,
Optimize, Mark Collected, dedup radius, pin radius, last event).

Implementation note: `LegolasSettingsViewModel` is a new VM holding the
extracted bindings; pre-existing sub-VMs (`InventoryGridSettingsViewModel`,
`LegolasColors` / `LegolasBrushes`) compose into it unchanged.

### Decision 3: Mode picker location

**Mode pick = wizard step 0**, ahead of `AwaitingPosition`. Wizard VM
exposes a `WizardStep` discriminator that gates on a `Mode` selection
before delegating to `SurveyFlowController` / `MotherlodeFlowController`.
A "Change mode" affordance stays visible from later steps so users
aren't trapped — clicking it returns to step 0 and resets the active
flow controller.

The dashboard, by contrast, keeps mode as tabs (orthogonal there — a
power user is mid-route in one mode and shouldn't be re-asked).

### Decision 4: Motherlode wizard depth

**Stay coarse for PR1; revisit after manual testing.** PR1 ships
Motherlode-in-wizard as a single Measuring screen mirroring today's
panel content. Once the Survey wizard has been driven through real
sessions, we'll know whether the Motherlode workflow needs derived
sub-states (option b in the originally-considered set) or whether the
existing layout is already clear. PR3 captures this revisit.

## Survey wizard — step-by-step copy

Wizard step is keyed off a `WizardStep` discriminator (in the wizard VM)
that resolves to either the synthetic `PickMode` step or the active
`SurveyFlowState`. Each step gets one `DataTemplate`. Controls delegate
to existing commands on `ControlPanelViewModel` / `MapOverlayViewModel`.

| Step | Title | Body | Primary action | Secondary |
|---|---|---|---|---|
| `PickMode` (synthetic, step 0) | **What are you doing?** | "Two flows: **Survey** (you're using survey items to find slabs / minerals) or **Motherlode** (you're trilaterating buried treasure from a Treasure Map)." | Survey · Motherlode (large buttons) | — |
| `AwaitingPosition` | **Show me where you are** | "Click on the map where your character is right now. We'll use this to translate the survey directions you'll see in chat." | Show overlays (if hidden) | Change mode |
| `Listening` | **Use a survey** | "Use any survey item from your bag. We'll watch the chat log for the `[Status]` line and place a pin for you." Below: list of pins placed so far + Optimize when ≥1. | Optimize Route (enabled when surveys ≥ 1) | Reset · Change mode |
| `AwaitingPin` | **Place this pin** | "Survey detected: **{Name}** at ~{X}E, {Y}N. Click on the map where the ping is showing in-game." | (none — click the map) | Cancel pending |
| `Gathering` | **Walk your route** | "Walk to each target in order. We'll mark them collected automatically. Note: new surveys are ignored until you reset (the projector is anchored to where you started)." | Mark current collected | Reset |
| `Done` | **All collected** | "Nice. Reset to start a new session — set your player position again first." | Reset | Change mode |

Notes:
- "Change mode" routes back to `PickMode` and resets the active flow
  controller. Available from any state where it isn't actively
  destructive (omitted on `AwaitingPin` to avoid mid-pin escape hatches).
- The "we ignore new surveys post-Optimize" copy in `Gathering` is
  load-bearing (see [legolas_position_anchor_constraint.md] memory
  context). Don't soften it.
- `AwaitingPin` should re-render `PendingSurvey.Name`/`Offset` reactively;
  the controller already raises `PhaseDescription` through `PendingSurvey`.
- Wizard footer always visible: "Use dashboard from now on" button (the
  exit ramp). Settings live in the shell tab now (Decision 2), so no
  in-wizard gear icon.

## Dashboard layout sketch (PR2)

Goal: condense today's panel to a single non-scrolling status strip + a
contextual action panel that tracks `SurveyFlow.CurrentState`. Roughly:

```
┌─────────────────────────────────────────────────────────────────┐
│  [Survey] [Motherlode]                                       ↻   │  ← mode tabs · view-mode toggle (settings live in shell tab)
├─────────────────────────────────────────────────────────────────┤
│  ▶ Listening  · 3 pins · last: "Survey: Iron Vein 12E 4N"        │  ← status strip (binds to CurrentState + LastLogEvent)
├─────────────────────────────────────────────────────────────────┤
│  [Set Player Position]  [Optimize Route]  [Mark Collected]       │  ← contextual buttons (enabled per CanX)
│  [Reset]                                                          │
├─────────────────────────────────────────────────────────────────┤
│  Pins                                                             │
│  ─────                                                            │
│   1. Iron Vein            12E   4N    ✔                          │
│   2. Slab of Quartz       18E  -3N                                │  ← active target highlighted
│   3. Garnet                4E  21N                                │
└─────────────────────────────────────────────────────────────────┘
```

Re-uses the existing surveys collection binding. Buttons use existing
commands; their enable predicates already exist as `SurveyFlow.CanOptimize`
etc. Settings groups (opacity, click-through, colors, grid) live in the
shell's per-module settings tab (Decision 2), reachable through the
existing module-settings affordance — not surfaced on the dashboard.

## Settings plumbing

Add to `LegolasSettings`:

```csharp
public LegolasUiMode UiMode { get; set; } = LegolasUiMode.Wizard;  // Decision 1
```

`LegolasUiMode { Wizard, Dashboard }` enum in `Domain/`. Add the enum
to `LegolasSettingsJsonContext` so the source-generated serializer
covers it. `INPC` on `UiMode` so the shell re-templates without restart.

(Originally drafted with a `HasSeenWizard` flag for a separate "skip
next time" affordance — dropped as YAGNI. The exit ramp flips `UiMode`
directly, which is sufficient.)

**"Use dashboard from now on" button in the wizard footer:** sets
`UiMode = Dashboard`. One button, discoverable.

**Module settings wiring (Decision 2):** `LegolasModule` exposes
`SettingsViewType = typeof(LegolasSettingsView)` (currently null). The
new `LegolasSettingsView` / `LegolasSettingsViewModel` host the four
extracted groups — overlay options, click-through, marker colors,
inventory grid. Existing sub-VMs (`InventoryGridSettingsViewModel`,
`LegolasColors`, `LegolasBrushes`) compose into the new VM unchanged.

The dedup radius and pin radius are session controls (they affect active
parsing/rendering immediately) and stay on the main panel for now.
They could move to settings later — flag during PR1 review.

## PR breakdown

1. **PR1 — Wizard + settings split.** This document's "scope of the
   first wizard PR" section. ~5 new files, ~3 modified.
2. **PR2 — Dashboard restyle.** Replaces the lifted-and-shifted dashboard
   content with the layout sketched above. No new behavior; cosmetic.
3. **PR3 — Motherlode wizard depth.** `MotherlodeWizardStep` derived
   enum + per-step templates. Lands once Survey wizard has been used a
   bit and we know what's working.

Each PR closes one or more of the umbrella-#4 sub-tasks. File a separate
issue for each before coding so the Roadmap shows them.

## Wizard VM behavior

`LegolasWizardViewModel` is the only new VM. It owns:

- `WizardStep CurrentStep` — `PickMode | AwaitingPosition | Listening |
  AwaitingPin | Gathering | Done | MotherlodeMeasuring`. Computed from
  `(Session.Mode, SurveyFlow.CurrentState, has-mode-been-picked-yet)`.
- `bool HasPickedMode` — false until step 0 commits a mode. Reset to
  false on `ChangeMode`.
- `PickSurveyMode()` / `PickMotherlodeMode()` / `ChangeMode()` commands.
- `UseDashboardFromNowOn()` command — flips `Settings.UiMode = Dashboard`.

Subscribes to `SurveyFlow.PropertyChanged` (re-compute step on
`CurrentState` change) and `Session.PropertyChanged` (re-compute on
`Mode` change in case the dashboard switches mode while the wizard is
also live).

`ChangeMode()` calls `SurveyFlow.Reset()` (or the Motherlode reset path),
clears `HasPickedMode`, returns to `PickMode`.

## Test strategy

PR1 needs a small test surface — most of the work is XAML-and-bindings:

- `LegolasUiModeRoundTripTests`: serialise/deserialise `LegolasSettings`
  with `UiMode = Wizard | Dashboard` through the source-generated context.
- `LegolasWizardViewModelTests`:
  - Initial state: `HasPickedMode = false`, `CurrentStep = PickMode`.
  - `PickSurveyMode()` → `HasPickedMode = true`, `CurrentStep` follows
    `SurveyFlow.CurrentState` (initially `AwaitingPosition`).
  - Driving `SurveyFlow` through `ConfirmPlayerPosition →
    OnSurveyDetected → ConfirmPin → OptimizeRoute → MarkCollected` and
    asserting `CurrentStep` re-projects each transition.
  - `PickMotherlodeMode()` → `CurrentStep = MotherlodeMeasuring`.
  - `ChangeMode()` from any post-pick step → resets to `PickMode`,
    `SurveyFlow` is back to `AwaitingPosition` (or `Listening` if
    `HasPlayerPosition` survives the controller's Reset semantics).
  - `UseDashboardFromNowOn()` flips settings `UiMode`.
- DI smoke test (`LegolasModuleTests` if one exists, otherwise add):
  resolving `LegolasWizardViewModel` and `LegolasSettingsViewModel` from
  the configured `IServiceProvider` succeeds.

UI-level testing remains manual — start the app, drive a session,
confirm the wizard re-templates as expected. The user's collaboration
style requires this verification before reporting done.

## Open questions

- **Reset confirmation.** Today the Start/Reset button is unconfirmed.
  In `Gathering`/`Done` states with collected pins, should the wizard
  prompt? Lean: no — these are short sessions, undo is "set position
  and start again". Mirrors the dashboard's behavior.
- **Light/dark theme.** Dashboard sketch above assumes the existing
  Mithril dark palette; verify against `Mithril.Shared.Wpf/Resources.xaml`
  brushes when implementing.
- **Animation between states.** Probably none — wizard re-templates can
  cross-fade if it feels too jarring, but start without and add only
  if user feedback demands it.
- **Dedup / pin radius on settings vs. panel.** Flagged in Settings
  plumbing — defer the call to PR1 review.
