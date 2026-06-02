# Gandalf — in-game clock alarm

**Tracked in:** #164

## Context

Project Gorgon has its own day/night cycle that advances at 12× wall-clock (1 in-game hour = 5 real minutes). The game's `IGameClock` is already wired and tested in [GameClock.cs](../../../src/Mithril.Shared/Game/GameClock.cs); the shell already displays the in-game time in 12-hour format.

Gandalf today only supports **countdown** timers (`StartedAt + Duration`). The user wants a second timer kind: **fire when the in-game clock reaches a target hour:minute** (e.g., "6:00 PM in-game"), with a per-timer toggle for one-shot vs recurring (re-arms each in-game day).

Why this works cleanly: Gandalf's just-redesigned scheduler ([TimerExpirationScheduler.cs](../../../src/Gandalf.Module/Services/TimerExpirationScheduler.cs)) is keyed by **real-time** `DateTimeOffset` — and the in-game-time → real-time inverse of the 12× formula is closed-form. So an in-game-time alarm just resolves to "the real-time instant the clock next reads HH:MM" and slots into the existing scheduler with no new event-loop machinery.

## Approach

**One central abstraction**: every timer row exposes a single `FiringAt: DateTimeOffset` instant that represents "when this Running timer fires." Countdowns compute `StartedAt + Duration`; game-clock timers compute the next in-game-time occurrence after `StartedAt`. The scheduler, view, row, and progress check all dispatch off `FiringAt` — kind-specific math happens once at Start/Restart.

**Schema bloat is contained**:
- `TimerProgress.FiringAt` is `[JsonIgnore]` (not persisted — recomputed on load so a future re-anchor of `GameClock` doesn't poison stored state).
- `TimerProgressEntry.FiringAt` is a nullable derived projection. User source stamps it; Quest/Loot leave it null. Consumers fall back to `StartedAt + Catalog.Duration` — so Quest/Loot/`DashboardAggregator` (and their tests) compile and behave identically to today.

## Files to modify

### 1. `IGameClock` — inverse formula

[GameClock.cs](../../../src/Mithril.Shared/Game/GameClock.cs)

Add to the interface and concrete class:

```csharp
DateTimeOffset NextOccurrence(GameTimeOfDay target, DateTimeOffset floor);
```

Returns the earliest real-time instant ≥ `floor` where the in-game clock reads `target`. One in-game day = 7200 real seconds, so occurrences recur on a 7200s real-time grid.

**Edge rule**: if the candidate firing instant is within 50 ms of `floor`, advance one full in-game day (`+7200s`). Same rationale as the floor at [TimerExpirationScheduler.cs:103](../../../src/Gandalf.Module/Services/TimerExpirationScheduler.cs#L103). Prevents fire-on-Start when the player presses Start at exactly the target time, which would otherwise double-fire recurring alarms.

**Reuse `AnchorUtc`/`AnchorGameSeconds`/`Ratio`/`SecondsPerGameDay` constants** — extract to `internal const` so the inverse uses the same literals as `GetCurrent`. Add unit tests in [GameClockTests.cs](../../../tests/Mithril.Shared.Tests/GameClockTests.cs).

### 2. `GandalfTimerDef` — trigger kind + game-time fields

[GandalfTimerDef.cs](../../../src/Gandalf.Module/Domain/GandalfTimerDef.cs)

```csharp
public enum GandalfTriggerKind { Countdown = 0, GameTimeOfDay = 1 }

public sealed class GandalfTimerDef
{
    public string Id { get; set; } = ...;
    public string Name { get; set; } = "";
    public GandalfTriggerKind Kind { get; set; } = GandalfTriggerKind.Countdown;
    public TimeSpan Duration { get; set; }            // Countdown
    public int? GameHour { get; set; }                // GameTimeOfDay (0–23)
    public int? GameMinute { get; set; }              // GameTimeOfDay (0–59)
    public bool Recurring { get; set; }               // GameTimeOfDay only in v1
    public string Region { get; set; } = "";
    public string Map { get; set; } = "";
}
```

Bump [GandalfDefinitions.Version](../../../src/Gandalf.Module/Domain/GandalfDefinitions.cs#L12) `1 → 2`. Identity migrate — `System.Text.Json` source-gen handles new optional fields cleanly; v1 files load with `Kind=Countdown`, null game fields, `Recurring=false`. Validate on save: if `Kind=GameTimeOfDay`, both `GameHour` and `GameMinute` must be set.

### 3. `TimerProgress` — cache `FiringAt`

[GandalfProgress.cs](../../../src/Gandalf.Module/Domain/GandalfProgress.cs)

```csharp
public sealed class TimerProgress
{
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonIgnore]
    public DateTimeOffset? FiringAt { get; set; }
}
```

Why `[JsonIgnore]`: the comment at [GameClock.cs:32–35](../../../src/Mithril.Shared/Game/GameClock.cs#L32-L35) explicitly anticipates re-anchoring. Persisting `FiringAt` for game-clock timers would silently rot if a future patch shifts the day/night cycle. Recompute on load via a `RehydrateFiringAt` step in `TimerProgressService` after `_view.Current` is first observed (and on `OnCurrentChanged`).

### 4. `TimerProgressService` — stamp on Start/Restart, dispatch on Check, re-arm on recurring

[TimerProgressService.cs](../../../src/Gandalf.Module/Services/TimerProgressService.cs)

- Inject `IGameClock`.
- Add `static DateTimeOffset ComputeFiringAt(GandalfTimerDef def, DateTimeOffset startedAt, IGameClock clock)`:
  - `Countdown` → `startedAt + def.Duration`
  - `GameTimeOfDay` → `clock.NextOccurrence(new GameTimeOfDay(def.GameHour ?? 0, def.GameMinute ?? 0), startedAt)`
- `Start`/`Restart` ([lines 70–104](../../../src/Gandalf.Module/Services/TimerProgressService.cs#L70)): stamp `progress.FiringAt = ComputeFiringAt(...)` after setting `StartedAt`.
- `CheckExpirations` ([lines 201–221](../../../src/Gandalf.Module/Services/TimerProgressService.cs#L201)): replace the `view.State == TimerState.Done` test with `now >= progress.FiringAt`. **Recurring branch** (highest-risk subtask):
  1. Fire `TimerExpired` first (so the alarm plays on the just-completed run).
  2. If `def.Kind == GameTimeOfDay && def.Recurring`, re-arm: `progress.StartedAt = progress.FiringAt; progress.CompletedAt = null; progress.FiringAt = ComputeFiringAt(def, progress.StartedAt.Value, _clock); _expiredNotified.Remove(id);` then `MarkDirty()`. **Forgetting `_expiredNotified.Remove` silently swallows the next fire** — add a unit test that drives two consecutive fires.
  3. Otherwise stamp `CompletedAt = now` as today.
- After `_view.Current` swap on `OnCurrentChanged`, walk progress and rehydrate `FiringAt`.

### 5. `TimerView` and `TimerRow` — use `FiringAt` everywhere

[TimerView.cs](../../../src/Gandalf.Module/Domain/TimerView.cs) — `State`/`Remaining`/`Fraction` use `Progress.FiringAt ?? (Progress.StartedAt + Def.Duration)` as the firing instant. Effective duration for `Fraction` denominator = `firingAt - StartedAt`.

[TimerRow.cs](../../../src/Gandalf.Module/Domain/TimerRow.cs) — add a single computed property `DateTimeOffset? FiringAt => Progress is null ? null : (Progress.FiringAt ?? Progress.StartedAt + Catalog.Duration);` and route `State` (line 32), `Remaining` (line 42), `CompletedAt` (line 52), `Fraction` (line 63), and `NextDisplayChangeAt` (line 96) through it. With the fallback, Quest/Loot rendering is byte-identical to today.

### 6. `TimerProgressEntry` — derived `FiringAt`

[ITimerSource.cs:67](../../../src/Gandalf.Module/Domain/ITimerSource.cs#L67)

```csharp
public sealed record TimerProgressEntry(
    string Key,
    DateTimeOffset StartedAt,
    DateTimeOffset? DismissedAt,
    DateTimeOffset? FiringAt = null);
```

[UserTimerSource.ProjectProgress](../../../src/Gandalf.Module/Services/UserTimerSource.cs#L108) stamps `FiringAt: p.FiringAt`. `QuestSource.SnapshotProgress`, `LootSource.SnapshotProgress`, and `DashboardAggregator` leave the parameter at default null — record equality in [TimerRowDeltaDiffer.ProgressEquals](../../../src/Gandalf.Module/Domain/TimerRowDeltaDiffer.cs) keeps working unchanged.

### 7. `TimerExpirationScheduler.ComputeSoonest` — read `FiringAt`

[TimerExpirationScheduler.cs:109–121](../../../src/Gandalf.Module/Services/TimerExpirationScheduler.cs#L109)

Replace `var expiresAt = p.StartedAt.Value + def.Duration;` with `var expiresAt = p.FiringAt ?? p.StartedAt.Value + def.Duration;`. Single-line change.

### 8. `TimerAlarmService` — fix recurring re-fire dedup

[TimerAlarmService.cs:22](../../../src/Gandalf.Module/Services/TimerAlarmService.cs#L22)

The current `HashSet<string> _firedKeys` swallows the second fire of the same key — a recurring game-clock alarm would alarm at 6 PM today and silently fail at 6 PM tomorrow.

Fix: change `_firedKeys` to `Dictionary<string, DateTimeOffset>` of last-fire-time. Gate re-fires on "not fired in the last ~30 s" (debounces accidental same-tick double-fires while allowing the ~2-real-hour recurring cadence). Forward-leaning for any future recurring source.

### 9. `ElapsedWhileAwayClassifier` — accept `firingAt`, not `duration`

[ElapsedWhileAwayClassifier.cs](../../../src/Gandalf.Module/Domain/ElapsedWhileAwayClassifier.cs)

Today the classifier takes `TimeSpan duration` and computes `theoreticalDone = started + duration` ([line 31](../../../src/Gandalf.Module/Domain/ElapsedWhileAwayClassifier.cs#L31)). For game-clock timers `Catalog.Duration` is meaningless — a recurring 6 PM alarm would produce the wrong "elapsed while away" badge.

Change the signature to:

```csharp
public static bool IsElapsedWhileAway(
    TimerProgress progress,
    DateTimeOffset firingAt,
    DateTimeOffset? lastActiveAt,
    DateTimeOffset now)
```

Body: `theoreticalDone = firingAt;` — caller is now responsible for picking the right firing instant.

Single call site: [TimerListViewModel.cs:218](../../../src/Gandalf.Module/ViewModels/TimerListViewModel.cs#L218). Swap `vm.Row.Duration` for `vm.Row.FiringAt` (the new computed property added in step 5). Skip rows where `vm.Row.FiringAt is null`.

Update [ElapsedWhileAwayClassifierTests.cs](../../../tests/Gandalf.Module.Tests/ElapsedWhileAwayClassifierTests.cs) to pass `firingAt = started + duration` for existing cases, and add one new case for a game-clock timer where `firingAt` is *not* `started + duration` (verifies the classifier no longer assumes a fixed catalog duration).

### 10. Dialog UI

[TimerDialogViewModel.cs](../../../src/Gandalf.Module/ViewModels/TimerDialogViewModel.cs) and [TimerDialogContent.xaml](../../../src/Gandalf.Module/Views/TimerDialogContent.xaml)

- Add a top "Trigger" radio: **Countdown** / **In-game time**.
- When **In-game time**: swap Hours/Minutes/Seconds for `GameHour` (0–23) + `GameMinute` (0–59) inputs; show a 12-hour preview using [`GameTimeOfDay.ToString12Hour()`](../../../src/Mithril.Shared/Game/GameClock.cs#L21); add **Recurring** checkbox.
- Apply the same `IsDurationEditable = isIdleOnActive` gate to the game-time inputs ([rationale at TimerDialogViewModel.cs:42–44](../../../src/Gandalf.Module/ViewModels/TimerDialogViewModel.cs#L42)).
- `OnPrimaryAction` validation: GameTimeOfDay requires both fields in valid range.

## Known v1 limitations (document, don't fix)

- "Edit while running" for `GameHour/GameMinute` is gated to idle-only, matching today's countdown rule. Re-stamping `FiringAt` mid-run for the active character is a UX win to revisit if anyone asks.

## Verification

**Unit tests**:
- `GameClockTests` — `NextOccurrence` round-trip: for many `target`/`floor` pairs, `clock.GetCurrent(at: returnedInstant) == target`. Edge: `floor` exactly at `target` → returns `floor + 7200s`. Edge: `floor` is one tick after `target` → returns `floor + 7199.99…s`.
- `TimerProgressService` — Start a `Countdown` (existing tests should pass unchanged with the `FiringAt` substitution). Start a `GameTimeOfDay` recurring with a fake `IGameClock`, advance fake `TimeProvider` past `FiringAt` once, assert `TimerExpired` fires; advance past the next in-game day, assert `TimerExpired` fires **again** (this is the regression test for the `_expiredNotified.Remove` and `_firedKeys` dedup fixes).
- `TimerExpirationScheduler` — assert `NextExpirationAt` for a GameTimeOfDay timer matches `IGameClock.NextOccurrence`.
- `TimerRow` — with `Progress.FiringAt = null`, behavior matches today's `Catalog.Duration` math (Quest/Loot regression guard).
- `ElapsedWhileAwayClassifier` — existing cases re-cast as `firingAt = started + duration`; new case where `firingAt ≠ started + duration` (game-clock semantics).

**End-to-end manual**:
1. `dotnet build Mithril.slnx` and `dotnet run --project src/Mithril.Shell`.
2. Create a `Countdown` timer with a 30 s duration. Confirm it fires (no regression).
3. Create a `GameTimeOfDay` timer with `Recurring=false`, target = the in-game time displayed in the shell + ~10 in-game minutes (~50 real seconds). Press Start. Confirm: idle list shows the target time; alarm fires at the right moment; row goes Done.
4. Same setup but `Recurring=true`. Confirm: alarm fires; row immediately re-arms (StartedAt updates, no Done state stuck on screen for >1 frame); 2 real hours later, alarm fires again.
5. Editing the def while Running for the active character: confirm hour/minute inputs are disabled (matches countdown behavior).
6. Restart the app while a GameTimeOfDay timer is Running: confirm it re-arms correctly on load (rehydrate path).
