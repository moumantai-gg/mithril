# Planning index

Specs and plans for Mithril features and agent tasks. Each row points at a slug folder under `docs/planning/<slug>/` whose contents are self-contained: a cold/spawned session can read the linked issue, follow the link to the slug folder, and have everything it needs.

## Convention

- One folder per effort, named with a human-readable slug (e.g. `gwaihir-v1.0`, `silmarillion-244-effects-tab`).
- Inside the folder, the canonical files are:
  - `spec.md` — what we're building and why; the problem statement, constraints, design decisions, "verification owed" markers.
  - `plan.md` — how we're building it; the step-by-step implementation plan, often with phases / checkpoints.
  - Supporting notes (review feedback, ratified deltas, supplementary investigations) live alongside as separate files (e.g. `feedback.md`, `design.md`, `review.md`, `phase3-design.md`).
- Specs and plans are durable. When implementation lands, the row's **status flips** — the folder is not deleted.
- True scratch (pre-commit thinking, throwaway analysis) belongs in `.claude/plans/` or `$env:TEMP`, **never** here.

## Status values

| Status | Meaning |
|--------|---------|
| `active` | Work in progress or queued for an upcoming PR |
| `shipped` | Implementation merged; folder preserved as living history |
| `deferred` | Scoped but parked — revisit later |
| `abandoned` | Decided not to do — rationale captured in the folder |

## How to add a row

When you create a new slug folder, append a row to the table below: `slug | status | issue/PR | one-line description`. Link the slug to its folder, link the issue/PR to GitHub.

## Index

| Slug | Status | Issue/PR | Description |
|------|--------|----------|-------------|
| [arda-state-snapshot-and-ui-dispatch](arda-state-snapshot-and-ui-dispatch/) | shipped | [#1011](https://github.com/moumantai-gg/mithril/issues/1011) · [#1013](https://github.com/moumantai-gg/mithril/issues/1013) | Fix Palantir pin-enum crash; `MapPins.Pins` snapshot + new `Arda.Wpf` `IUiEventSubscriber` + `WpfMapPinPresenter` |
| [calibration-1005-scale-aware-gate](calibration-1005-scale-aware-gate/) | shipped | [#1005](https://github.com/moumantai-gg/mithril/issues/1005) · [#1023](https://github.com/moumantai-gg/mithril/pull/1023) | Map auto-calibration: scale-aware monotonicity gate — don't reject a re-capture taken at a different in-game zoom |
| [gandalf-164-in-game-clock-alarm](gandalf-164-in-game-clock-alarm/) | shipped | [#164](https://github.com/moumantai-gg/mithril/issues/164) | Gandalf: fire timers at PG in-game time-of-day |
| [legolas-wizard](legolas-wizard/) | shipped | [#111](https://github.com/moumantai-gg/mithril/issues/111) · [#112](https://github.com/moumantai-gg/mithril/issues/112) · [#113](https://github.com/moumantai-gg/mithril/issues/113) | Legolas wizard view + dashboard rework + Motherlode wizard depth |
| [map-calibration-detection-project-split](map-calibration-detection-project-split/) | active | _no issue_ | Extract `Mithril.MapCalibration.Detection` project; OpenCv allowlist moves there, `.Capture` re-becomes Win32-only |
| [quest-discovery-module](quest-discovery-module/) | deferred | _no issue_ | Quest browser / eligibility surface (separate from Gandalf) |
| [samwise-alarm-channels](samwise-alarm-channels/) | shipped | _no issue_ | Samwise alarm channels — per-stage loop + collision behavior |
| [silmarillion-207-reference-browser](silmarillion-207-reference-browser/) | shipped | [#207](https://github.com/moumantai-gg/mithril/issues/207) | New module: reference-data browser (Items + Recipes v1) |
| [silmarillion-244-effects-tab](silmarillion-244-effects-tab/) | shipped | [#244](https://github.com/moumantai-gg/mithril/issues/244) | Silmarillion: Effects tab (Bucket B — paired with Abilities) |
| [silmarillion-249-storagevaults-tab](silmarillion-249-storagevaults-tab/) | shipped | [#249](https://github.com/moumantai-gg/mithril/issues/249) | Silmarillion: StorageVaults tab (Bucket B — long-tail) |
| [silmarillion-270-recipe-keyword-chip-to-items](silmarillion-270-recipe-keyword-chip-to-items/) | shipped | [#270](https://github.com/moumantai-gg/mithril/issues/270) | Recipe-detail keyword chip → Items tab keyword filter (symmetry close) |
| [silmarillion-404-visual-grammar](silmarillion-404-visual-grammar/) | shipped | [#404](https://github.com/moumantai-gg/mithril/issues/404) | Design system: fact / control / link visual grammar + migration |
| [silmarillion-407-source-dedup](silmarillion-407-source-dedup/) | shipped | [#407](https://github.com/moumantai-gg/mithril/issues/407) | Silmarillion coverage: dedup same entity surfaced twice across source headers |
| [silmarillion-412-treasure-detail](silmarillion-412-treasure-detail/) | active | [#412](https://github.com/moumantai-gg/mithril/issues/412) | Silmarillion: Treasure System tab — Power catalog + Profile pools |
| [silmarillion-polish-v1](silmarillion-polish-v1/) | shipped | [#229](https://github.com/moumantai-gg/mithril/issues/229) · [#231](https://github.com/moumantai-gg/mithril/issues/231) · [#234](https://github.com/moumantai-gg/mithril/issues/234) · [#239](https://github.com/moumantai-gg/mithril/issues/239) | Silmarillion polish v1 + DeepLink + Navigator registry refactors |
| [smaug-385-minfavortier-retype](smaug-385-minfavortier-retype/) | shipped | [#385](https://github.com/moumantai-gg/mithril/issues/385) | Smaug: converge `string?` favor → typed `FavorTier` |
