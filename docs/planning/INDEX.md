# Planning index

Specs and plans for Mithril features and agent tasks. Each row points at a slug folder under `docs/planning/<slug>/` whose contents are self-contained: a cold/spawned session can read the linked issue, follow the link to the slug folder, and have everything it needs.

## Convention

- One folder per effort, named with a human-readable slug (e.g. `gwaihir-v1.0`, `silmarillion-244-effects-tab`).
- Inside the folder, the canonical files are:
  - `spec.md` — what we're building and why; the problem statement, constraints, design decisions, "verification owed" markers.
  - `plan.md` — how we're building it; the step-by-step implementation plan, often with phases / checkpoints.
  - Supporting notes (review feedback, ratified deltas, supplementary investigations) live alongside as separate files.
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
| _(migration of `docs/agent-plans/*` pending — see [#TBD](#))_ | | | |
