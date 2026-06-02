# Quest discovery module

**Tracked in:** _no issue yet_

A future module dedicated to "what can I do right now" — a quest browser /
eligibility surface that's deliberately separate from Gandalf. Captured
here so the design we hashed out during the 2026-04-30 chain-quest
debugging doesn't get lost.

## Why a separate module

Gandalf is a *timer* module — it speaks the language of "this thing has a
cooldown clock running, here's how long." Trying to also answer "what
quests do I qualify for right now" inside Gandalf's Quests tab produced
two pressures pulling in opposite directions:

- **Cooldown awareness** wants the row visible only when the player has
  observed a completion (otherwise the tab fills with irrelevant rows).
- **Discovery** wants every takeable quest visible regardless of past
  completions.

Splitting the modules lets each surface stay narrow and honest. Per the
[Mithril design principles](https://github.com/moumantai-gg/mithril/wiki/Mithril-Design-Principles)
note, Gandalf does not re-evaluate eligibility — and a quest browser by
definition has to. They're different jobs.

## Scope sketch

The discovery module owns:

1. **Catalog projection.** Project `IReferenceDataService.Quests` into a
   filterable view: by `FavorNpc`, `DisplayedLocation`, level range,
   keywords, repeatable vs one-shot.
2. **Eligibility evaluation.** Compute "can I take this right now" by
   evaluating every requirement type the game uses
   (`MinFavorLevel`, `MinSkillLevel`, `QuestCompleted`,
   `QuestCompletedRecently`, `MinDelayAfterFirstCompletion*`,
   `HasEffectKeyword`, `IsVampire`, etc.) against character state. This
   IS authority-shopping, but that's the module's job. Stale conclusions
   are tolerable here in a way they aren't for Gandalf.
3. **Cross-link with Gandalf cooldowns.** When evaluating
   `QuestCompletedRecently`, look at Gandalf's progress rows — the
   `StartedAt` of the gating quest's row says when it last fired. Same
   for the Reuse* clock on the candidate quest itself.
4. **Chain awareness in the UI.** See "Stacked card rendering" below.

What the discovery module does **not** own: per-character state mutation
on observed completions (still Gandalf's responsibility — discovery
*reads* Gandalf's progress, doesn't write to it).

## Chain-card stacked-UI design

Captured during the 2026-04-30 design conversation. Applies to the
discovery module's renderer for repeatable quests; Gandalf's flat row
list stays as-is.

### Problem

Chained dailies (e.g. Orrrilund's `KilltheDoctrineKeeperRepeat` ↔
`KilltheDenMotherRepeat` pair, gated via `QuestCompletedRecently`) are
one *gameplay loop* but two *quest entries*. Rendering them as two
flat cards bloats the UI and obscures the cycle structure. Rendering
them as a single fused row hides per-quest data (rewards, names, partial
progress).

### Approach: stacked cards with vertical-dot navigation

For any chain (connected component on the `QuestCompletedRecently`
graph), render a single visual *stack* in the layout, with:

- **Top card:** the chain member with the most recent completion, ticking
  down its own `Reuse*` cooldown.
- **Stacked underneath:** the other chain members, peeking out enough at
  the edges that the user knows there are more cards.
- **Vertical dots on the left side:** one dot per card in the chain, the
  active card's dot highlighted. Clickable to navigate; ready cards get
  an accent (color or sparkle) so a takeable card under the stack
  surfaces visually.

### Data model

`QuestCatalogPayload` (or its discovery-module equivalent) gains a
`LinkedToInternalName` field projected from any `QuestCompletedRecently`
requirement. Connected components computed once at catalog build time
yield the chain membership.

### Card-only-when-completed rule

A chain card only renders when *at least one* member has a Gandalf
progress row. Pure-discovery rendering (everything you qualify for, even
unstarted chains) is a different filter, optionally toggle-able. The
default cooldown view stays observation-driven.

### Dashboard semantics

Each chain contributes one tile. The tile shows the top card's
`ReadyAt`. Title is the chain identity (`FavorNpc` +
`DisplayedLocation` works — e.g. "Orrrilund — Ranalon Den"). No bloat.

### Implementation cost (rough)

- Data: small. `LinkedToInternalName` projection + connected-components
  pass.
- View-model: medium. `ChainRowViewModel { Cards, TopIndex,
  SelectedIndex }`, top-card auto-reset on a chain member completing.
- View (XAML): medium-to-large. Custom card-stack template with peek-out
  edges, side dot strip with hit testing. Day-and-a-half ish if no
  surprises.

## Open questions

- Does the chain abstraction also handle longer chains (3+ members)?
  Mechanically yes (it's just a connected component); UX might need
  tuning if real chains exist past length 2.
- Should the discovery module duplicate Gandalf's Pending journal state
  or read it as a foreign service? Probably read.
- Is there an existing Project Gorgon equivalent surface we should
  emulate (the in-game journal? a popular addon?), or are we
  green-fielding the UX?
