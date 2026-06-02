# Gandalf Roadmap

Gandalf owns user-defined timers today (alarm/snooze/dismiss/sound/flash, global-def + per-character-progress shape, split-migration of legacy single-character data). The next major capability is **repeatable-quest timers** — surfacing each quest's `ReuseTime` cooldown in the same timer feed as user-defined timers.

> **Active backlog:** [Mithril Roadmap Project — `Module: Gandalf`](https://github.com/users/arthur-conde/projects/3/views/1?filterQuery=module%3A%22Gandalf%22).

## Why repeatable-quest timers

Project Gorgon's repeatable quests have a `ReuseTime` (minutes/hours/days) before the same quest can be re-accepted. The game has no in-UI affordance telling you when a given quest is ready — players keep mental notes, alarm apps, or accept/abandon-loop to probe readiness.

Mithril already projects every quest's reuse timer in [QuestEntry.cs:24-26](../src/Mithril.Shared/Reference/QuestEntry.cs#L24-L26). Two log signals from `IPlayerLogStream` close the loop:

- `ProcessLoadQuest(?, questId)` — fires on login for every quest currently in the player's journal.
- `ProcessCompleteQuest(?, questId)` — fires on completion. Combined with `QuestEntry.Reuse*`, this is the cooldown clock.

## Lives in Gandalf, not a new module

Gandalf already owns the full alarm pipeline ([TimerAlarmService.cs](../src/Gandalf.Module/Services/TimerAlarmService.cs)) and the global-def + per-character-progress shape ([GandalfTimerDef.cs](../src/Gandalf.Module/Domain/GandalfTimerDef.cs) + [GandalfProgress.cs](../src/Gandalf.Module/Domain/GandalfProgress.cs)) that quest cooldowns map onto cleanly. Standing up a parallel module would re-implement ~80% of Gandalf.

The cost is splitting Gandalf's timer store into two feeds (user-defined + quest-derived) producing the same `GandalfTimerDef`-shaped row. `TimerProgressService` and `TimerAlarmService` then consume both feeds without caring about origin.

## State model — re-derive on login, persist what logs can lose

- **Accepted list** — rebuilt every session from `ProcessLoadQuest` replay. Authoritative for *what's currently in your journal*. If the player abandoned a quest while Mithril was offline, replay shows the truth. **Not persisted.**
- **`CompletedAt` / `DismissedAt`** — persisted per `(character, questKey)` via `JsonSettingsStore<T>`. Survives log rotation, which is the gap log-replay can't fill: if `Player.log` has rolled past the completion event, the cooldown clock is unrecoverable from logs alone.

Per-character scoping uses the existing `IActiveCharacterService` / `CharacterPresenceService` pattern Samwise and Bilbo follow.

## UI — second timer feed alongside user-defined timers

Gandalf gains a second timer feed alongside today's user-defined timers. Both render through the existing `TimerListView` / `TimerListViewModel`; quest-derived rows are read-only on duration (driven by `QuestEntry.Reuse*`) and add per-row affordances:

- **Filter chips**: Pending (accepted, not completed) · Cooling (completed, not yet ready) · Ready (cooldown elapsed).
- **Bulk select** — checkbox per row; toolbar `Dismiss Selected`, `Dismiss All Ready`, `Dismiss All`.
- **Dismiss** = mark `DismissedAt = now`. Row disappears from visible list but stays in the store, so a later `ProcessCompleteQuest` resurrects it with a fresh cooldown.

## v1 scope — pure-`ReuseTime` quests only

v1 only handles quests where `ReuseTime` is the sole cooldown gate. The long tail — `MinDelayAfterFirstCompletion_Hours`, account-flagged repeats, NPC re-prompt requirements, weekly/daily wall-clock resets — is deferred. Filter the candidate list by `QuestEntry.Reuse* != null && Requirements doesn't contain time-flavored gates`.

This keeps v1 honest: every cooldown shown is a cooldown the parser can compute correctly. Misleading timers erode trust faster than absent ones do.

## Architectural dependencies

The feature pulls on three pieces of cross-cutting plumbing. State of each:

1. **Audio playback in `Mithril.Shared`** — *shipped in [PR #28](https://github.com/moumantai-gg/mithril/pull/28)*. `AudioPlayer`, `IPlaybackHandle`, `WindowFlasher` now live in Shared; Gandalf consumes them without a Samwise dependency.
2. **Two-feed timer source** — *not shipped*. The single `TimerDefinitionsService` needs to compose user-defined and quest-derived feeds into one stream of `GandalfTimerDef`-shaped rows. Likely an `ITimerSource` abstraction or a parallel definitions service joined at `TimerListViewModel`.
3. **Past-anchored start times in `TimerProgressService`** — *not shipped*. [`TimerProgressService.StartedAt`](../src/Gandalf.Module/Services/TimerProgressService.cs) is unconditionally set to `DateTimeOffset.UtcNow` on Start. Quest cooldowns need `StartedAt = T_complete` (a past timestamp) — confirm whether the existing service can express that, or whether it needs a new entrypoint.

(2) and (3) get filed as separate issues; the v1 quest-timer issue depends on both.

## Non-goals

- **Typed `QuestRequirement` parsing.** The current flat projection ([QuestRequirement record](../src/Mithril.Shared/Reference/QuestEntry.cs#L45-L51)) is enough for display + log-line matching. Discriminated requirement records are a precondition for *eligibility evaluation* ("can the player re-take this right now, given their skills/favor?") that a future quest DB or favor planner needs — but the timer itself doesn't. Tracked as issue [#12](https://github.com/moumantai-gg/mithril/issues/12).
- **Shell-scoped notification/inbox subsystem.** A persisted-ack-shaped inbox is the right model for quest readiness (cooldowns are long; the player may be away from Mithril for hours after readiness fires). The v1 timer ships with local dismiss persistence inside Gandalf. Defer the shell inbox until a second feature (Samwise ripeness, future Pippin work, etc.) creates real cross-module demand.
- **Quests-as-database module.** Searching "what favor quests can I do for Sie Antry that I qualify for?" is a separate, larger feature requiring typed requirements + a character snapshot. Don't bundle it with the timer.

## Open design questions

- **Log replay UX on first launch.** `IPlayerLogStream` already replays the rolling buffer on subscribe. With the persisted `CompletedAt` store seeded empty on first-ever launch, the player will see no quest timers until they next complete a quest under Mithril's watch. Acceptable, or do we want a one-time "scan as far back as `Player-prev.log` goes" pass to backfill?
- **Naming.** "Gandalf · Repeatable Quests" vs. a dedicated tab name. The module identity becomes "timers" once two feeds exist; possibly worth dropping the user-vs-quest distinction in the UI and just showing one merged list with a small icon distinguishing source. UX call.

## History

- **2026-04-29** — [PR #28](https://github.com/moumantai-gg/mithril/pull/28): audio playback (`AudioPlayer` / `IPlaybackHandle` / `WindowFlasher`) lifted from Samwise/Gandalf into `Mithril.Shared`. Architectural prereq #1; unblocks the quest-timer pipeline by removing the cross-module dependency.
