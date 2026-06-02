# Arda map-pin snapshot + `Arda.Wpf` UI-dispatch — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `WorldStateViewModel.RefreshPins()` from crashing via a cross-thread `List<T>` enumeration race, by snapshotting `IMapPinState.Pins` at the source and migrating Palantir's pin-display VM onto two new `Arda.Wpf` primitives (`IUiEventSubscriber` and `WpfMapPinPresenter`) that handle dispatcher marshalling + crash-safety once for all WPF consumers.

**Architecture:** Three layers, smallest fix at each.

1. `MapPins.Pins` / `MapScope.Pins` return `_pins.ToArray()` — every off-thread reader gets a safe snapshot. Producer-side determinism unchanged.
2. `IUiEventSubscriber` in `Arda.Wpf` wraps `IDomainEventSubscriber`, posts each handler through `Dispatcher.InvokeAsync` inside a try/catch that logs via `ILogger`. Non-UI subscribers (Legolas coordinators, replay tests) stay on `IDomainEventSubscriber` and see no change.
3. `WpfMapPinPresenter` in `Arda.Wpf` is the sister of `InventoryProjection` — subscribes via `IUiEventSubscriber`, owns an `ObservableCollection<MapPinEntry>` on the UI thread, applies adds/removes/area-change deltas with coord-keyed dedup. Palantir consumes it and deletes its own `_dispatch` machinery.

**Tech Stack:** .NET 10, C# latest, WPF (`net10.0-windows`), CommunityToolkit.Mvvm, xunit + FluentAssertions. Build via `dotnet build Mithril.slnx`; test via `dotnet test`.

**Design spec:** [spec.md](spec.md) — every task references its spec section.

---

## PR organisation — two PRs for review efficiency

The work splits along a natural boundary that maps to reviewer concerns:

| | Scope | Files | Reviewer's question | Urgency |
|---|---|---|---|---|
| **PR-A** | `MapPins.Pins` snapshot-on-read | 3 (1 src + 1 contract doc + 1 test) | "Does this snapshot break Arda determinism?" | Ships the crash fix |
| **PR-B** | `Arda.Wpf` `IUiEventSubscriber` + `WpfMapPinPresenter` + Palantir VM migration | ~8 (3 new src + 1 csproj + 2 tests + 3 Palantir) | "Is the new abstraction right? Is the Palantir migration correct?" | No urgency — crash already fixed by PR-A |

PR-B branches off `main` **after PR-A has merged** (don't stack — keep each PR reviewable against a known main). PR-B's spec-§"Acceptance" item that requires the `MapPins` snapshot is satisfied by the merged PR-A.

**Tracked issues:** two — one per PR.

| | Title (suggested) | Labels |
|---|---|---|
| Issue-A | "Palantir crash: List enumeration race on `IMapPinState.Pins`" | `area:arda`, `module:palantir`, `type:bug` |
| Issue-B | "Promote pin display to `Arda.Wpf` (`IUiEventSubscriber` + `WpfMapPinPresenter`); migrate Palantir off `Action<Action>` dispatch" | `area:arda`, `module:palantir`, `type:refactor` |

**Branches:**
- PR-A: `fix/arda-pin-snapshot`
- PR-B: `refactor/arda-wpf-ui-dispatch`

---

## File Structure

### Created files

| Path | Responsibility | Task |
|---|---|---|
| `src/Arda/Arda.Wpf/UiEventSubscriber.cs` | `IUiEventSubscriber` interface + `WpfUiEventSubscriber` impl + `SafeInvoke` helper | T3, T4 |
| `src/Arda/Arda.Wpf/SyncUiEventSubscriber.cs` | Synchronous test-double impl (also useful from any sync consumer) | T5 |
| `src/Arda/Arda.Wpf/WpfMapPinPresenter.cs` | Bindable `ObservableCollection<MapPinEntry>` projection of `IMapPinState` | T7 |
| `tests/Arda.World.Player.Tests/MapPinsSnapshotTests.cs` | Pins property returns a snapshot, not a live view | T1 |
| `tests/Arda.Wpf.Tests/UiEventSubscriberTests.cs` | `SafeInvoke` exception-swallow + `SyncUiEventSubscriber` round-trip | T3, T5 |
| `tests/Arda.Wpf.Tests/WpfMapPinPresenterTests.cs` | Seed + add/remove/area-change/upsert/dedup | T6 |

### Modified files

| Path | Change | Task |
|---|---|---|
| `src/Arda/Arda.World.Player/Internal/MapPins.cs:27-28` | `Pins` / `PinsList` return `_pins.ToArray()` | T2 |
| `src/Arda/Arda.Wpf/Arda.Wpf.csproj` | Add `Microsoft.Extensions.Logging.Abstractions` PackageReference | T3 |
| `src/Palantir.Module/ViewModels/WorldStateViewModel.cs` | Drop `_dispatch`/`RefreshPins`/`DefaultDispatch`/parallel pin collection/`MapPinRow` record; consume `IUiEventSubscriber` + `WpfMapPinPresenter`; `Pins`/`PinCount`/`HasPins` become thin pass-throughs over the presenter | T8 |
| `src/Palantir.Module/Views/WorldStateView.xaml` | Pin section binds to `Presenter.Pins`; in-template formatting (no `MapPinRow` projection) | T9 |
| `src/Palantir.Module/PalantirModule.cs` | Register `IUiEventSubscriber`, `WpfMapPinPresenter` (singletons) | T10 |
| `tests/Palantir.Tests/WorldStateViewModelTests.cs` | Replace `Action<Action>` sync dispatcher with `SyncUiEventSubscriber`; pin assertions move to `presenter.Pins` | T11 |

### Verified unchanged (regression-only)

`src/Arda/Arda.World.Player/Internal/MapScope.cs:26` — `pins.PinsList` is now an array snapshot, no code edit. Legolas pin consumers (`CharacterPinAnchor`, `PinCalibrationCoordinator`, `MotherlodeViewModel`, `MotherlodeMeasurementCoordinator`) read `_pinState.Pins`; the snapshot fix protects them; no code touches required. `InventoryProjection` uses a different state-change shape; out of scope.

---

## Commit map

### PR-A (branch `fix/arda-pin-snapshot`)

| Commit | Tasks | Description |
|---|---|---|
| A1 | T0A | INDEX.md row added (status `active`, links Issue-A) |
| A2 | T1–T2 | `MapPins.Pins` snapshot-on-read (kills the crash) |

Two commits, both atomic, both build+test green. Open + merge PR-A before starting PR-B. (`T0A`'s docs-only commit lands inside PR-A so the planning artefact is part of the merged history.)

### PR-B (branch `refactor/arda-wpf-ui-dispatch`, branches from `main` after PR-A merges)

| Commit | Tasks | Description |
|---|---|---|
| B1 | T3–T5 | `IUiEventSubscriber` interface + `WpfUiEventSubscriber` + `SyncUiEventSubscriber` + tests |
| B2 | T6–T7 | `WpfMapPinPresenter` + tests |
| B3 | T8–T11 | Palantir `WorldStateViewModel` migration + XAML + DI + test refactor |
| B4 | T12 | INDEX.md status flipped to `shipped` (post-merge) |

Each commit builds + tests green standalone — the reviewer can read a commit at a time and stop between any two without holding extra state.

---

# PR-A — `MapPins.Pins` snapshot-on-read

> Branch: `fix/arda-pin-snapshot` (off `main`). Tasks T0A → T1 → T2 → T-CloseA.
> Reviewer focus: snapshot semantics; determinism of the Arda producer path.

## Task 0A: File Issue-A + add INDEX.md row

**Files:**
- Create branch `fix/arda-pin-snapshot`
- Modify: `docs/planning/INDEX.md` (the row already exists; just set the Issue-A reference)

- [ ] **Step 1: Create the branch**

```
git switch -c fix/arda-pin-snapshot
```

- [ ] **Step 2: File Issue-A**

```
gh issue create \
  --title "Palantir crash: List enumeration race on IMapPinState.Pins" \
  --label "area:arda,module:palantir,type:bug" \
  --body-file - <<'EOF'
WorldStateViewModel.RefreshPins() crashes with InvalidOperationException
("Collection was modified; enumeration operation may not execute")
rethrown by the finalizer thread as AggregateException.

Root cause: Arda.World.Player.Internal.MapPins exposes its backing
List<MapPinEntry> directly via IMapPinState.Pins. Any consumer
enumerating it off the Arda ingest thread races the ingest thread's
_pins.Add / RemoveAt. Six consumer sites currently read this property
off-thread (1 in Palantir + 5 in Legolas) — see
docs/planning/arda-state-snapshot-and-ui-dispatch/spec.md §Survey.

Fix: snapshot at the source. Pins / PinsList return _pins.ToArray() on
each read. Mutation path unchanged.

Crash log:

```
System.AggregateException: A Task's exception(s) were not observed...
 ---> System.InvalidOperationException: Collection was modified;
      enumeration operation may not execute.
   at System.Collections.Generic.List`1.Enumerator.MoveNext()
   at Palantir.ViewModels.WorldStateViewModel.RefreshPins()
   at Palantir.ViewModels.WorldStateViewModel.<>c__DisplayClass39_0.<OnPinAdded>b__0()
   at System.Windows.Threading.DispatcherOperation.InvokeDelegateCore()
   at System.Windows.Threading.DispatcherOperation.InvokeImpl()
```

PR-A is the source-side snapshot fix (this issue). PR-B (separate)
migrates Palantir off the racy dispatch pattern entirely via new
Arda.Wpf primitives — see Issue-B (filed when PR-A merges).

Design + plan: docs/planning/arda-state-snapshot-and-ui-dispatch/
EOF
```

Note the issue number returned. Used as `#NNN-A` in the steps below.

- [ ] **Step 3: Set the INDEX.md row's Issue/PR reference**

Edit `docs/planning/INDEX.md`. The row currently reads
`| ... | active | _issue pending_ | ... |`. Replace `_issue pending_` with
`[#NNN-A](https://github.com/moumantai-gg/mithril/issues/NNN-A)` using the
real issue number.

- [ ] **Step 4: Commit (commit A1)**

```
git add docs/planning/INDEX.md
git commit -m "$(cat <<'EOF'
docs(planning): link arda-state-snapshot-and-ui-dispatch to tracking issue

Index row references Issue-A (#NNN-A); PR-A (snapshot fix) follows.
Spec + plan unchanged.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 1: `MapPinsSnapshotTests` — pin the snapshot contract (failing test first)

**Files:**
- Create: `tests/Arda.World.Player.Tests/MapPinsSnapshotTests.cs`

Spec §"Approach #1" + Acceptance #6. TDD: write the test before the snapshot change so we see the race-prone behaviour fail, then the snapshot make it pass.

- [ ] **Step 1: Look up the existing `MapPinTests.cs` neighbour** to copy its dispatch-bus + dispatch-table fixture pattern.

Run:
```
type tests\Arda.World.Player.Tests\MapPinTests.cs
```
Skim the test file's set-up: it constructs an `IDomainEventPublisher`, instantiates `MapPins`, and drives `IFrameHandler.Handle` on the `PinAddHandler` / `PinRemoveHandler`. Mirror the same fixture.

- [ ] **Step 2: Add the failing test file**

```csharp
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

/// <summary>
/// Pins the snapshot contract on <see cref="IMapPinState.Pins"/>. The crash that
/// motivated this came from a consumer enumerating the property while the Arda
/// ingest thread mutated the backing list; the fix is to return a snapshot, so a
/// captured collection must be unaffected by subsequent mutations. See
/// docs/planning/arda-state-snapshot-and-ui-dispatch/spec.md.
/// </summary>
public sealed class MapPinsSnapshotTests
{
    private static LogLineMetadata Meta()
        => new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsReplay: false);

    [Fact]
    public void Pins_ReturnsSnapshot_NotLiveView()
    {
        var bus = new RecordingPublisher();
        var pins = new MapPins(bus);

        // Add one pin.
        pins.PinAddHandler.Handle(
            args: "1, 0, 1, (100.00, 0.00, 200.00), \"first\"".AsSpan(),
            verb: "ProcessMapPinAdd".AsSpan(),
            sourceLog: "Player.log",
            metadata: Meta());

        // Capture the collection reference *before* mutating again.
        var captured = pins.Pins;
        captured.Should().HaveCount(1);

        // Mutate the underlying state (the racing scenario).
        pins.PinAddHandler.Handle(
            args: "1, 0, 1, (300.00, 0.00, 400.00), \"second\"".AsSpan(),
            verb: "ProcessMapPinAdd".AsSpan(),
            sourceLog: "Player.log",
            metadata: Meta());

        // The previously-captured collection MUST NOT reflect the new pin.
        // If Pins returns _pins directly, captured.Count == 2 here and the test fails.
        captured.Should().HaveCount(1, "Pins is a snapshot at the moment of read");
        pins.Pins.Should().HaveCount(2, "a fresh read sees the up-to-date set");
    }

    [Fact]
    public void PinsList_ReturnsSnapshot_NotLiveView()
    {
        var bus = new RecordingPublisher();
        var pins = new MapPins(bus);

        pins.PinAddHandler.Handle(
            "1, 0, 1, (100.00, 0.00, 200.00), \"first\"".AsSpan(),
            "ProcessMapPinAdd".AsSpan(),
            "Player.log",
            Meta());

        var captured = pins.PinsList;

        pins.PinAddHandler.Handle(
            "1, 0, 1, (300.00, 0.00, 400.00), \"second\"".AsSpan(),
            "ProcessMapPinAdd".AsSpan(),
            "Player.log",
            Meta());

        captured.Should().HaveCount(1);
        pins.PinsList.Should().HaveCount(2);
    }

    private sealed class RecordingPublisher : IDomainEventPublisher
    {
        public void Publish<T>(T domainEvent) where T : struct { }
    }
}
```

- [ ] **Step 3: Run the test; verify both cases fail with `captured.Count == 2`**

Run:
```
dotnet test tests\Arda.World.Player.Tests\Arda.World.Player.Tests.csproj --filter "FullyQualifiedName~MapPinsSnapshotTests" -v normal
```
Expected: both tests FAIL — `captured.Should().HaveCount(1)` reports `captured.Count == 2` because `_pins` is exposed directly. This is the bug.

If a test compiles-but-doesn't-fail, double-check that `MapPins`, `RecordingPublisher`, and `PinAddHandler.Handle`'s signatures match the actual API (peek at `tests\Arda.World.Player.Tests\MapPinTests.cs` for the canonical shape).

---

## Task 2: `MapPins.Pins` / `PinsList` snapshot-on-read

**Files:**
- Modify: `src/Arda/Arda.World.Player/Internal/MapPins.cs:27-28`

Spec §"Approach #1".

- [ ] **Step 1: Apply the snapshot edit**

In `src/Arda/Arda.World.Player/Internal/MapPins.cs`, replace lines 27–28:

```csharp
// Before:
public IReadOnlyCollection<MapPinEntry> Pins => _pins;
internal IReadOnlyList<MapPinEntry> PinsList => _pins;

// After:
public IReadOnlyCollection<MapPinEntry> Pins => _pins.ToArray();
internal IReadOnlyList<MapPinEntry> PinsList => _pins.ToArray();
```

Also update the XML doc comment on `IMapPinState.Pins` in `src/Arda/Arda.Contracts/State/Player/IMapPinState.cs:14-17` to make the snapshot promise explicit:

```csharp
/// <summary>
/// Active pins in the current area, keyed by (X, Z) coordinate. Each call
/// returns a fresh snapshot — consumers may enumerate the result from any
/// thread without coordinating with the Arda ingest thread.
/// </summary>
IReadOnlyCollection<MapPinEntry> Pins { get; }
```

- [ ] **Step 2: Run the snapshot tests; verify they now pass**

Run:
```
dotnet test tests\Arda.World.Player.Tests\Arda.World.Player.Tests.csproj --filter "FullyQualifiedName~MapPinsSnapshotTests" -v normal
```
Expected: both tests PASS.

- [ ] **Step 3: Run the full `Arda.World.Player.Tests` suite to confirm no regression**

Run:
```
dotnet test tests\Arda.World.Player.Tests\Arda.World.Player.Tests.csproj
```
Expected: all tests pass. (`MapPins`'s mutation path and `MapScope`'s exposure are unchanged; only the read shape changed.)

- [ ] **Step 4: Commit (commit A2 — closes PR-A's code change)**

```
git add src/Arda/Arda.World.Player/Internal/MapPins.cs \
        src/Arda/Arda.Contracts/State/Player/IMapPinState.cs \
        tests/Arda.World.Player.Tests/MapPinsSnapshotTests.cs
git commit -m "$(cat <<'EOF'
fix(arda): MapPins.Pins / PinsList return a snapshot, not the live list

The backing List<MapPinEntry> was exposed directly via IMapPinState.Pins,
so any consumer enumerating it off the Arda ingest thread raced the
ingest thread's _pins.Add / RemoveAt. Palantir.WorldStateViewModel's
dispatcher-marshalled RefreshPins() hit this in practice and crashed
with a finalizer-rethrown AggregateException("Collection was modified;
enumeration operation may not execute").

Pins / PinsList now return _pins.ToArray() — a fresh snapshot per read,
safe to enumerate from any thread. Pin counts are tiny (handful per
area) so the allocation cost is negligible. Mutation path unchanged
(single-threaded on the ingest thread); Arda's producer-side
determinism is unaffected (replay tests pass unchanged).

Doc-comment on IMapPinState.Pins updated to make the snapshot promise
part of the contract.

PR-B (filed separately when this merges) migrates Palantir off the
underlying dispatch pattern entirely via new Arda.Wpf primitives
(IUiEventSubscriber + WpfMapPinPresenter). This PR is the source-side
fix that protects all six current off-thread reader sites; PR-B is
the consumer-side cleanup.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2.5: Manual verify + open PR-A + merge

**Files:** none (push + GitHub workflow).

- [ ] **Step 1: Manual verification (the crash reproduction)**

Run:
```
dotnet run --project src\Mithril.Shell
```

With PG also running and logged in, switch to Palantir → World State, then rapidly add/remove pins in-game and switch areas twice. No crash dialog should appear; pin list updates may still flicker (PR-A doesn't fix the clear-and-refill pattern — PR-B does) but the process must stay alive.

Close the app.

- [ ] **Step 2: Push branch and open PR-A**

```
git push -u origin fix/arda-pin-snapshot
gh pr create \
  --title "fix(arda): MapPins.Pins / PinsList return a snapshot, not the live list" \
  --body-file - <<'EOF'
## Summary

- Stops `Palantir.WorldStateViewModel.RefreshPins()` from crashing the process via a cross-thread `List<MapPinEntry>` enumeration race against the Arda ingest thread. (The unobserved `DispatcherOperation` Task captured the exception; finalizer rethrew as `AggregateException`.)
- `MapPins.Pins` and `MapPins.PinsList` now return `_pins.ToArray()` on each call — a fresh snapshot, safe to enumerate from any thread.
- Contract doc on `IMapPinState.Pins` updated to make the snapshot promise explicit.

This is **PR-A** of a two-PR effort:
- PR-A (this) — source-side fix. Tiny, low-risk, protects all six current off-thread reader sites (1 Palantir + 5 Legolas, all surveyed in the spec).
- PR-B (follows) — consumer-side cleanup: new `Arda.Wpf` primitives (`IUiEventSubscriber` + `WpfMapPinPresenter`) and the Palantir VM migration. No urgency; this PR already fixes the crash.

## Reviewer focus

The single load-bearing question: **does the snapshot break Arda's producer-side determinism?** Answer: no — mutation path (`OnAdd` / `OnRemove`) is unchanged, single-threaded on the ingest thread; only the read path returns an isolated copy. `Arda.World.Player.Tests` passes unchanged (proves replay-test determinism).

## Test plan

- [x] `dotnet test tests/Arda.World.Player.Tests` all green (new `MapPinsSnapshotTests` + existing `MapPinTests` + replay suite).
- [x] `dotnet build Mithril.slnx` is 0 warnings / 0 errors.
- [x] Manual repro of the original crash (rapid pin add/remove in PG with Palantir World State tab open) — no longer reproduces.

Design + plan: [`docs/planning/arda-state-snapshot-and-ui-dispatch/`](../tree/fix/arda-pin-snapshot/docs/planning/arda-state-snapshot-and-ui-dispatch).

Closes #NNN-A.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Replace `NNN-A` with Issue-A's number.

- [ ] **Step 3: Get PR-A merged**

Wait for CI green + review. Address review feedback if any. Once merged: PR-A is done; switch to PR-B.

---

# PR-B — `Arda.Wpf` UI-dispatch primitives + Palantir VM migration

> Branch: `refactor/arda-wpf-ui-dispatch` (off `main` **after** PR-A merges). Tasks T0B → T3 → T4 → T5 → T6 → T7 → T8 → T9 → T10 → T11 → T12.
> Reviewer focus: shape of the new abstractions; correctness of the Palantir VM rewrite; that non-WPF Arda consumers (Legolas, replay) stay on the old path.

## Task 0B: New branch + file Issue-B

**Files:** none on disk; GitHub workflow.

- [ ] **Step 1: Sync local main and branch from it**

```
git switch main
git pull --ff-only
git switch -c refactor/arda-wpf-ui-dispatch
```

The merged PR-A snapshot fix is now on `main` — PR-B is built on it.

- [ ] **Step 2: File Issue-B**

```
gh issue create \
  --title "Promote pin display to Arda.Wpf primitives; migrate Palantir off Action<Action> dispatch" \
  --label "area:arda,module:palantir,type:refactor" \
  --body-file - <<'EOF'
Follow-on from #NNN-A. The crash root cause was fixed by the MapPins
snapshot; this issue covers the consumer-side cleanup that prevents
the entire bug class from re-appearing in any future WPF consumer.

New abstractions (Arda.Wpf):

  - IUiEventSubscriber — wraps IDomainEventSubscriber, posts handlers
    through Dispatcher.InvokeAsync inside a SafeInvoke try/catch that
    logs via ILogger instead of crashing via finalizer rethrow.
  - WpfMapPinPresenter — sister to InventoryProjection. Bindable
    ObservableCollection<MapPinEntry> on the UI thread, coord-keyed
    dedup, area-change clear.

Palantir.WorldStateViewModel migrated: drops Action<Action> _dispatch
field + DefaultDispatch static + parallel pin collection + RefreshPins.
XAML binds straight to the presenter's collection.

Non-WPF consumers (Legolas coordinators, replay tests, world-sim
driver) stay on IDomainEventSubscriber unchanged; Arda's producer-side
determinism is preserved.

Design + plan: docs/planning/arda-state-snapshot-and-ui-dispatch/
EOF
```

Note the returned `#NNN-B`.

---

## Task 3: `IUiEventSubscriber` interface + `SafeInvoke` helper + tests

**Files:**
- Create: `src/Arda/Arda.Wpf/UiEventSubscriber.cs`
- Modify: `src/Arda/Arda.Wpf/Arda.Wpf.csproj`
- Create: `tests/Arda.Wpf.Tests/UiEventSubscriberTests.cs`

Spec §"Approach #2".

- [ ] **Step 1: Add `Microsoft.Extensions.Logging.Abstractions` to `Arda.Wpf.csproj`**

Open `src/Arda/Arda.Wpf/Arda.Wpf.csproj`. In the `<ItemGroup>` that already has `<PackageReference Include="CommunityToolkit.Mvvm" />`, add:

```xml
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
```

Version is centrally managed (`Directory.Packages.props:7`); no version attribute needed.

- [ ] **Step 2: Write the failing test for `SafeInvoke` and the sync round-trip**

Create `tests/Arda.Wpf.Tests/UiEventSubscriberTests.cs`:

```csharp
using Arda.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Wpf.Tests;

public sealed class UiEventSubscriberTests
{
    [Fact]
    public void SafeInvoke_HandlerSucceeds_RunsAndDoesNotLog()
    {
        var logger = new RecordingLogger();
        var ran = false;

        UiEventSubscriber.SafeInvoke<TestEvent>(_ => ran = true, new TestEvent(42), logger);

        ran.Should().BeTrue();
        logger.Errors.Should().BeEmpty();
    }

    [Fact]
    public void SafeInvoke_HandlerThrows_LogsError_DoesNotPropagate()
    {
        var logger = new RecordingLogger();
        Exception? thrown = null;

        try
        {
            UiEventSubscriber.SafeInvoke<TestEvent>(
                _ => throw new InvalidOperationException("boom"),
                new TestEvent(1),
                logger);
        }
        catch (Exception ex) { thrown = ex; }

        thrown.Should().BeNull("SafeInvoke must swallow handler exceptions to keep them off the finalizer thread");
        logger.Errors.Should().ContainSingle()
            .Which.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("boom");
    }

    [Fact]
    public void SyncUiEventSubscriber_DeliversEvents_OnPublishingThread()
    {
        var bus = new TestBus();
        var sub = new SyncUiEventSubscriber(bus);
        var seen = new List<TestEvent>();

        using var _ = sub.Subscribe<TestEvent>(seen.Add);
        bus.Publish(new TestEvent(7));
        bus.Publish(new TestEvent(8));

        seen.Should().HaveCount(2);
        seen[0].Value.Should().Be(7);
        seen[1].Value.Should().Be(8);
    }

    [Fact]
    public void SyncUiEventSubscriber_Dispose_StopsDelivery()
    {
        var bus = new TestBus();
        var sub = new SyncUiEventSubscriber(bus);
        var seen = new List<TestEvent>();
        var token = sub.Subscribe<TestEvent>(seen.Add);

        bus.Publish(new TestEvent(1));
        token.Dispose();
        bus.Publish(new TestEvent(2));

        seen.Should().ContainSingle().Which.Value.Should().Be(1);
    }

    private readonly record struct TestEvent(int Value);

    private sealed class TestBus : IDomainEventPublisher, IDomainEventSubscriber
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public void Publish<T>(T evt) where T : struct
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) return;
            foreach (var d in list.ToArray()) ((Action<T>)d).Invoke(evt);
        }

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new();
            list.Add(handler);
            return new Sub(() => list.Remove(handler));
        }

        private sealed class Sub(Action onDispose) : IDisposable
        {
            private Action? _onDispose = onDispose;
            public void Dispose()
            {
                _onDispose?.Invoke();
                _onDispose = null;
            }
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Errors { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
                Errors.Add((logLevel, exception, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
```

- [ ] **Step 3: Run the test; verify it fails to compile**

Run:
```
dotnet build tests\Arda.Wpf.Tests\Arda.Wpf.Tests.csproj
```
Expected: build FAILS — `UiEventSubscriber`, `SyncUiEventSubscriber` not found.

- [ ] **Step 4: Implement `UiEventSubscriber.cs`**

Create `src/Arda/Arda.Wpf/UiEventSubscriber.cs`:

```csharp
using System.Windows.Threading;
using Arda.Contracts;
using Microsoft.Extensions.Logging;

namespace Arda.Wpf;

/// <summary>
/// UI-thread-affined wrapper over <see cref="IDomainEventSubscriber"/>.
/// Subscribers receive their handlers on the WPF Dispatcher thread,
/// inside a try/catch that logs (rather than crashing via the finalizer
/// thread when an unobserved <see cref="DispatcherOperation"/> Task captures
/// the exception).
///
/// <para>Non-UI subscribers should continue to depend on
/// <see cref="IDomainEventSubscriber"/> directly — their lock-based
/// coordination relies on synchronous handler firing on the Arda ingest
/// thread, which this wrapper deliberately breaks.</para>
///
/// <para>Determinism note: this wrapper sits at the consumer edge, *after*
/// <c>bus.Publish</c> returns. It does not affect Arda's producer-side
/// determinism. Headless contexts (replay tests, world-sim driver) do not
/// reference Arda.Wpf and never see this type.</para>
/// </summary>
public interface IUiEventSubscriber
{
    /// <summary>Subscribe to domain events of type <typeparamref name="T"/>.
    /// The handler runs on the WPF Dispatcher thread, inside a try/catch that
    /// logs handler exceptions instead of crashing the process.</summary>
    IDisposable Subscribe<T>(Action<T> handler) where T : struct;
}

/// <summary>
/// Production <see cref="IUiEventSubscriber"/> backed by a WPF
/// <see cref="Dispatcher"/>. See the interface doc for the determinism /
/// non-UI-subscriber boundary.
/// </summary>
public sealed class WpfUiEventSubscriber : IUiEventSubscriber
{
    private readonly IDomainEventSubscriber _inner;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<WpfUiEventSubscriber> _logger;

    public WpfUiEventSubscriber(
        IDomainEventSubscriber inner,
        Dispatcher dispatcher,
        ILogger<WpfUiEventSubscriber> logger)
    {
        _inner = inner;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        => _inner.Subscribe<T>(evt => _dispatcher.InvokeAsync(
            () => SafeInvoke(handler, evt, _logger)));
}

/// <summary>
/// Shared static helpers exposed so unit tests can drive the exception-swallow
/// path without needing an STA-affinitised <see cref="Dispatcher"/>.
/// </summary>
public static class UiEventSubscriber
{
    /// <summary>
    /// Invoke <paramref name="handler"/> on <paramref name="evt"/>; if it
    /// throws, log the exception via <paramref name="logger"/> and swallow it.
    /// The contract: this method MUST NOT propagate exceptions (that's the
    /// whole point — keep them off the finalizer thread).
    /// </summary>
    public static void SafeInvoke<T>(Action<T> handler, T evt, ILogger logger)
        where T : struct
    {
        try { handler(evt); }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "UI handler for {Event} threw on the dispatcher; suppressed " +
                "to prevent finalizer-thread crash.",
                typeof(T).Name);
        }
    }
}
```

- [ ] **Step 5: Verify the SafeInvoke tests fail just on `SyncUiEventSubscriber` not existing yet**

Run:
```
dotnet build tests\Arda.Wpf.Tests\Arda.Wpf.Tests.csproj
```
Expected: still failing — `SyncUiEventSubscriber` not found. That lands in T5.

---

## Task 4: `WpfUiEventSubscriber` is feature-complete (no extra code; sanity-build only)

`WpfUiEventSubscriber` was already written inside `UiEventSubscriber.cs` in T3. It's exercised end-to-end by the manual repro in T12; we don't unit-test it directly because the STA + dispatcher fixture would cost more than it adds — `SafeInvoke` covers the exception-swallow contract, and `SyncUiEventSubscriber` covers the subscribe/dispose contract. The WPF impl is a 5-line delegation over the two.

- [ ] **Step 1: Confirm `Arda.Wpf` builds standalone**

Run:
```
dotnet build src\Arda\Arda.Wpf\Arda.Wpf.csproj
```
Expected: build PASSES (interface + impl + helper are self-contained).

---

## Task 5: `SyncUiEventSubscriber` + finish the T3 test suite

**Files:**
- Create: `src/Arda/Arda.Wpf/SyncUiEventSubscriber.cs`

Spec §"Approach #2".

- [ ] **Step 1: Create `SyncUiEventSubscriber.cs`**

```csharp
using Arda.Contracts;

namespace Arda.Wpf;

/// <summary>
/// Synchronous <see cref="IUiEventSubscriber"/> — handlers run on whatever
/// thread <see cref="IDomainEventPublisher.Publish{T}"/> is called from.
/// Intended for unit tests (replaces the legacy <c>Action&lt;Action&gt;</c>
/// sync dispatcher pattern) and headless integration smoke tests; do NOT
/// register this in the WPF shell — pin updates would land on the Arda
/// ingest thread instead of the UI thread.
/// </summary>
public sealed class SyncUiEventSubscriber : IUiEventSubscriber
{
    private readonly IDomainEventSubscriber _inner;

    public SyncUiEventSubscriber(IDomainEventSubscriber inner) => _inner = inner;

    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        => _inner.Subscribe(handler);
}
```

- [ ] **Step 2: Run the T3 test suite; verify all four pass**

Run:
```
dotnet test tests\Arda.Wpf.Tests\Arda.Wpf.Tests.csproj --filter "FullyQualifiedName~UiEventSubscriberTests" -v normal
```
Expected: all four tests PASS (SafeInvoke success / SafeInvoke throws / Sync round-trip / Sync dispose).

- [ ] **Step 3: Commit (commit B1)**

```
git add src/Arda/Arda.Wpf/UiEventSubscriber.cs \
        src/Arda/Arda.Wpf/SyncUiEventSubscriber.cs \
        src/Arda/Arda.Wpf/Arda.Wpf.csproj \
        tests/Arda.Wpf.Tests/UiEventSubscriberTests.cs
git commit -m "$(cat <<'EOF'
feat(arda.wpf): IUiEventSubscriber — UI-affinitised, crash-safe wrapper

Adds two implementations of a new IUiEventSubscriber abstraction to
Arda.Wpf:

  - WpfUiEventSubscriber wraps IDomainEventSubscriber, posts each handler
    through Dispatcher.InvokeAsync, and runs the handler inside a
    SafeInvoke try/catch so a misbehaving handler logs via ILogger instead
    of crashing the process via the unobserved-task finalizer-thread
    rethrow.

  - SyncUiEventSubscriber forwards subscribe-and-publish synchronously —
    the test seam for VMs that previously took an Action<Action> sync
    dispatcher.

Non-UI subscribers (Legolas coordinators, L4 composers, replay tests,
world-sim driver) keep depending on IDomainEventSubscriber and see no
behavioural change. Arda's producer-side determinism is unchanged; this
wrapper sits at the consumer edge, after bus.Publish returns.

Companion to the merged PR-A (MapPins snapshot); the WpfMapPinPresenter
in commit B2 consumes IUiEventSubscriber and the Palantir VM migration
in commit B3 ditches its Action<Action> _dispatch field.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: `WpfMapPinPresenterTests` — failing tests first

**Files:**
- Create: `tests/Arda.Wpf.Tests/WpfMapPinPresenterTests.cs`

Spec §"Approach #3".

- [ ] **Step 1: Write the tests**

```csharp
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Xunit;

namespace Arda.Wpf.Tests;

public sealed class WpfMapPinPresenterTests
{
    private static readonly DateTimeOffset T =
        new(2026, 5, 18, 10, 45, 47, TimeSpan.Zero);

    private static LogLineMetadata Meta() => new(T, T, IsReplay: false);

    [Fact]
    public void Seeds_FromInitialPinState_OnConstruction()
    {
        var state = new FakePinState(
            new MapPinEntry(100, 200, "alpha", Shape: 0, Color: 1),
            new MapPinEntry(300, 400, "beta", Shape: 1, Color: 4));
        var bus = new TestBus();

        using var presenter = new WpfMapPinPresenter(state, new SyncUiEventSubscriber(bus));

        presenter.Pins.Should().HaveCount(2);
        presenter.Pins.Select(p => p.Label).Should().BeEquivalentTo("alpha", "beta");
    }

    [Fact]
    public void MapPinAdded_NewCoord_AppendsRow()
    {
        var state = new FakePinState();
        var bus = new TestBus();
        using var presenter = new WpfMapPinPresenter(state, new SyncUiEventSubscriber(bus));

        bus.Publish(new MapPinAdded(150, 250, "gamma", Shape: 0, Color: 2, Meta()));

        presenter.Pins.Should().ContainSingle()
            .Which.Label.Should().Be("gamma");
    }

    [Fact]
    public void MapPinAdded_ExistingCoord_ReplacesInPlace()
    {
        var state = new FakePinState();
        var bus = new TestBus();
        using var presenter = new WpfMapPinPresenter(state, new SyncUiEventSubscriber(bus));

        bus.Publish(new MapPinAdded(150, 250, "old-label", Shape: 0, Color: 2, Meta()));
        bus.Publish(new MapPinAdded(150.001, 250.001, "new-label", Shape: 0, Color: 3, Meta()));
        // 150.001 / 250.001 are within MapPins' 0.01 coord tolerance — same key.

        presenter.Pins.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Label = "new-label",
                Color = 3,
            });
    }

    [Fact]
    public void MapPinRemoved_KnownCoord_DropsRow()
    {
        var state = new FakePinState();
        var bus = new TestBus();
        using var presenter = new WpfMapPinPresenter(state, new SyncUiEventSubscriber(bus));

        bus.Publish(new MapPinAdded(150, 250, "gamma", 0, 2, Meta()));
        bus.Publish(new MapPinRemoved(150, 250, "gamma", Meta()));

        presenter.Pins.Should().BeEmpty();
    }

    [Fact]
    public void MapPinRemoved_UnknownCoord_IsNoOp()
    {
        var state = new FakePinState();
        var bus = new TestBus();
        using var presenter = new WpfMapPinPresenter(state, new SyncUiEventSubscriber(bus));

        bus.Publish(new MapPinAdded(150, 250, "gamma", 0, 2, Meta()));
        bus.Publish(new MapPinRemoved(999, 999, "ghost", Meta()));

        presenter.Pins.Should().ContainSingle()
            .Which.Label.Should().Be("gamma");
    }

    [Fact]
    public void AreaChanged_ClearsAllPins()
    {
        var state = new FakePinState();
        var bus = new TestBus();
        using var presenter = new WpfMapPinPresenter(state, new SyncUiEventSubscriber(bus));

        bus.Publish(new MapPinAdded(150, 250, "a", 0, 2, Meta()));
        bus.Publish(new MapPinAdded(350, 450, "b", 1, 4, Meta()));
        bus.Publish(new AreaChanged(PreviousArea: "AreaSerbule", NewArea: "AreaKur", Meta()));

        presenter.Pins.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_UnsubscribesAndStopsDeliveringEvents()
    {
        var state = new FakePinState();
        var bus = new TestBus();
        var presenter = new WpfMapPinPresenter(state, new SyncUiEventSubscriber(bus));

        bus.Publish(new MapPinAdded(150, 250, "before-dispose", 0, 2, Meta()));
        presenter.Pins.Should().ContainSingle();

        presenter.Dispose();
        bus.Publish(new MapPinAdded(350, 450, "after-dispose", 1, 4, Meta()));

        presenter.Pins.Should().ContainSingle()
            .Which.Label.Should().Be("before-dispose");
    }

    private sealed class FakePinState : IMapPinState
    {
        private readonly List<MapPinEntry> _pins;
        public FakePinState(params MapPinEntry[] seed) => _pins = new(seed);
        public IReadOnlyCollection<MapPinEntry> Pins => _pins.ToArray();
    }

    private sealed class TestBus : IDomainEventPublisher, IDomainEventSubscriber
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public void Publish<T>(T evt) where T : struct
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) return;
            foreach (var d in list.ToArray()) ((Action<T>)d).Invoke(evt);
        }

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new();
            list.Add(handler);
            return new Sub(() => list.Remove(handler));
        }

        private sealed class Sub(Action onDispose) : IDisposable
        {
            private Action? _onDispose = onDispose;
            public void Dispose() { _onDispose?.Invoke(); _onDispose = null; }
        }
    }
}
```

- [ ] **Step 2: Verify the tests fail to compile**

Run:
```
dotnet build tests\Arda.Wpf.Tests\Arda.Wpf.Tests.csproj
```
Expected: build FAILS — `WpfMapPinPresenter` not found.

---

## Task 7: `WpfMapPinPresenter` implementation

**Files:**
- Create: `src/Arda/Arda.Wpf/WpfMapPinPresenter.cs`

Spec §"Approach #3".

- [ ] **Step 1: Create `WpfMapPinPresenter.cs`**

```csharp
using System.Collections.ObjectModel;
using Arda.World.Player;
using Arda.World.Player.Events;

namespace Arda.Wpf;

/// <summary>
/// UI-thread projection of <see cref="IMapPinState"/>. Subscribes via
/// <see cref="IUiEventSubscriber"/> so every collection mutation happens
/// on the WPF Dispatcher thread, eliminating the cross-thread enumeration
/// race against the Arda ingest thread.
///
/// <para>Pin identity is the rounded (X, Z) coordinate, matching
/// <c>MapPins.OnRemove</c>'s remove-by-coord semantics (0.01 tolerance).
/// Re-adding the same coord with a different label/colour replaces the
/// existing row in place — binding clients see a single mutation rather
/// than remove+add flicker.</para>
///
/// <para>Lifetime: singleton per shell. One WPF UI thread → one
/// ObservableCollection; multiple consumers may bind to the same instance
/// safely.</para>
/// </summary>
public sealed class WpfMapPinPresenter : IDisposable
{
    private readonly Dictionary<(long X, long Z), MapPinEntry> _byCoord = new();
    private readonly IDisposable _addedSub;
    private readonly IDisposable _removedSub;
    private readonly IDisposable _areaSub;

    /// <summary>The current area's pins. Mutated only on the UI thread.</summary>
    public ObservableCollection<MapPinEntry> Pins { get; } = new();

    public WpfMapPinPresenter(IMapPinState state, IUiEventSubscriber bus)
    {
        // Seed first — IMapPinState.Pins is a snapshot (PR-A landed on main),
        // so this enumeration is safe regardless of which thread we're on.
        // Events that land between seed and Subscribe are delivered as deltas;
        // the coord-keyed dictionary upserts so a doubled Add is a no-op.
        foreach (var p in state.Pins) Upsert(p, addToCollection: true);

        _addedSub = bus.Subscribe<MapPinAdded>(OnAdded);
        _removedSub = bus.Subscribe<MapPinRemoved>(OnRemoved);
        _areaSub = bus.Subscribe<AreaChanged>(OnAreaChanged);
    }

    // Matches MapPins.OnRemove's coord-equality test: Math.Abs(< 0.01).
    // Round to centi-units so logically-equal coords share a key.
    private static (long X, long Z) Key(double x, double z)
        => ((long)Math.Round(x * 100), (long)Math.Round(z * 100));

    private void Upsert(MapPinEntry pin, bool addToCollection)
    {
        var key = Key(pin.X, pin.Z);
        var existed = _byCoord.ContainsKey(key);
        _byCoord[key] = pin;

        if (!addToCollection) return;

        if (existed)
        {
            for (var i = 0; i < Pins.Count; i++)
            {
                if (Key(Pins[i].X, Pins[i].Z) == key) { Pins[i] = pin; return; }
            }
        }
        Pins.Add(pin);
    }

    private void OnAdded(MapPinAdded e)
        => Upsert(new MapPinEntry(e.X, e.Z, e.Label, e.Shape, e.Color), addToCollection: true);

    private void OnRemoved(MapPinRemoved e)
    {
        var key = Key(e.X, e.Z);
        if (!_byCoord.Remove(key)) return;
        for (var i = 0; i < Pins.Count; i++)
        {
            if (Key(Pins[i].X, Pins[i].Z) == key) { Pins.RemoveAt(i); return; }
        }
    }

    private void OnAreaChanged(AreaChanged _)
    {
        // Per StateResetHandler: pins are per-area; on area transition
        // MapPins.Reset() clears the underlying list. Mirror that here.
        _byCoord.Clear();
        Pins.Clear();
    }

    public void Dispose()
    {
        _addedSub.Dispose();
        _removedSub.Dispose();
        _areaSub.Dispose();
    }
}
```

- [ ] **Step 2: Run the T6 test suite; verify all seven pass**

Run:
```
dotnet test tests\Arda.Wpf.Tests\Arda.Wpf.Tests.csproj --filter "FullyQualifiedName~WpfMapPinPresenterTests" -v normal
```
Expected: all seven tests PASS (seed / add new / add replaces / remove known / remove unknown / area cleared / dispose unsubscribes).

- [ ] **Step 3: Run the full `Arda.Wpf.Tests` suite**

Run:
```
dotnet test tests\Arda.Wpf.Tests\Arda.Wpf.Tests.csproj
```
Expected: all tests pass (T3 suite + T6 suite + existing InventoryProjection tests).

- [ ] **Step 4: Commit (commit B2)**

```
git add src/Arda/Arda.Wpf/WpfMapPinPresenter.cs \
        tests/Arda.Wpf.Tests/WpfMapPinPresenterTests.cs
git commit -m "$(cat <<'EOF'
feat(arda.wpf): WpfMapPinPresenter — bindable map-pin projection

Sister to InventoryProjection. Subscribes via IUiEventSubscriber to
MapPinAdded / MapPinRemoved / AreaChanged, maintains an
ObservableCollection<MapPinEntry> on the UI thread, applies deltas with
coord-keyed dedup (0.01 tolerance, matching MapPins.OnRemove's
remove-by-coord semantics). Re-adding the same coord replaces the
existing row in place so binding clients see a single mutation rather
than remove+add flicker.

Seeds from IMapPinState.Pins on construction; the PR-A snapshot fix
means that enumeration is safe regardless of which thread the presenter
is constructed on.

Consumer in commit B3 (Palantir.WorldStateViewModel).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Migrate `WorldStateViewModel` — implementation

**Files:**
- Modify: `src/Palantir.Module/ViewModels/WorldStateViewModel.cs`

Spec §"Approach #4".

- [ ] **Step 1: Replace the file with the migrated version**

Replace the entire body of `WorldStateViewModel.cs` with:

```csharp
using System.Globalization;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Arda.Wpf;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;

namespace Palantir.ViewModels;

/// <summary>
/// Debug surface over Arda's live world state: position, area, map pins,
/// celestial (moon phase), and weather. State is read from Arda state
/// interfaces (<see cref="IPositionState"/>, <see cref="IAreaState"/>, etc.)
/// and kept current via domain event subscriptions through
/// <see cref="IUiEventSubscriber"/> — every handler runs on the WPF
/// Dispatcher thread inside a SafeInvoke try/catch, so a misbehaving
/// handler cannot crash the process via an unobserved-task finalizer
/// rethrow.
///
/// <para>Pin rendering is delegated to <see cref="WpfMapPinPresenter"/>;
/// the VM only carries the count + observed-at timestamp for display.</para>
/// </summary>
public sealed partial class WorldStateViewModel : ObservableObject, IDisposable
{
    private readonly IPositionState _position;
    private readonly IAreaState _area;
    private readonly IMapPinState _pinState;
    private readonly ICelestialState _celestial;
    private readonly IWeatherState _weather;
    private readonly IReferenceDataService? _refData;
    private readonly WpfMapPinPresenter _pinPresenter;

    private IDisposable? _positionSub;
    private IDisposable? _areaSub;
    private IDisposable? _pinAddedSub;
    private IDisposable? _pinRemovedSub;
    private IDisposable? _celestialSub;
    private IDisposable? _weatherSub;
    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _pinsChangedHandler;

    [ObservableProperty] private string _areaKey = "(unknown)";
    [ObservableProperty] private string _areaFriendlyName = "(area not yet known)";
    [ObservableProperty] private string _areaShortName = "";
    [ObservableProperty] private bool _areaResolved;

    [ObservableProperty] private bool _hasPosition;
    [ObservableProperty] private string _positionText = "(no position observed yet)";
    [ObservableProperty] private string _measuredAtText = "—";
    [ObservableProperty] private string _positionSourceText = "—";

    [ObservableProperty] private string _pinsObservedAtText = "—";

    [ObservableProperty] private bool _hasMoonPhase;
    [ObservableProperty] private string _moonPhaseText = "(no celestial info observed yet)";
    [ObservableProperty] private string _moonPhaseRawText = "—";
    [ObservableProperty] private string _moonMeasuredAtText = "—";

    [ObservableProperty] private bool _hasWeather;
    [ObservableProperty] private string _weatherConditionText = "(weather unknown for this map)";
    [ObservableProperty] private string _weatherObservedAtText = "—";

    /// <summary>The presenter's UI-thread <see cref="WpfMapPinPresenter.Pins"/>.
    /// XAML binds directly; the VM no longer rebuilds a parallel collection.</summary>
    public System.Collections.ObjectModel.ObservableCollection<MapPinEntry> Pins => _pinPresenter.Pins;

    public int PinCount => Pins.Count;
    public bool HasPins => Pins.Count > 0;

    public WorldStateViewModel(
        IPositionState position,
        IAreaState area,
        IMapPinState pins,
        ICelestialState celestial,
        IWeatherState weather,
        IUiEventSubscriber bus,
        WpfMapPinPresenter pinPresenter,
        IReferenceDataService? refData = null)
    {
        _position = position;
        _area = area;
        _pinState = pins;
        _celestial = celestial;
        _weather = weather;
        _refData = refData;
        _pinPresenter = pinPresenter;

        SeedFromState();

        _positionSub = bus.Subscribe<PlayerPositionChanged>(OnPosition);
        _areaSub = bus.Subscribe<AreaChanged>(OnAreaChanged);
        _pinAddedSub = bus.Subscribe<MapPinAdded>(OnPinAdded);
        _pinRemovedSub = bus.Subscribe<MapPinRemoved>(OnPinRemoved);
        _celestialSub = bus.Subscribe<CelestialInfoChanged>(OnCelestial);
        _weatherSub = bus.Subscribe<WeatherChanged>(OnWeather);

        // Pin count/HasPins shadow the presenter's collection; flip change
        // notification when its size changes. Handler stored in a field so
        // Dispose can unsubscribe — the presenter is a singleton, so leaving
        // the inline lambda subscribed would pin the VM in memory.
        _pinsChangedHandler = (_, _) =>
        {
            OnPropertyChanged(nameof(PinCount));
            OnPropertyChanged(nameof(HasPins));
        };
        _pinPresenter.Pins.CollectionChanged += _pinsChangedHandler;
    }

    private void SeedFromState()
    {
        RefreshArea();

        if (_position.X is not null)
        {
            HasPosition = true;
            PositionText = FormatPosition(_position.X.Value, _position.Y ?? 0, _position.Z ?? 0);
        }

        if (_celestial.Phase != MoonPhase.Unknown || _celestial.CurrentPhaseRaw is not null)
        {
            HasMoonPhase = true;
            MoonPhaseText = _celestial.DisplayName ?? "(unknown phase)";
            MoonPhaseRawText = _celestial.Phase == MoonPhase.Unknown
                ? $"{_celestial.CurrentPhaseRaw} (unrecognised token)"
                : _celestial.CurrentPhaseRaw ?? "—";
            MoonMeasuredAtText = FormatTimestamp(_celestial.MeasuredAt);
        }

        if (_weather.CurrentWeather is { } w)
        {
            HasWeather = true;
            WeatherConditionText = w;
        }
    }

    private void OnPosition(PlayerPositionChanged e)
    {
        HasPosition = true;
        PositionText = FormatPosition(e.X, e.Y, e.Z);
        MeasuredAtText = FormatTimestamp(e.Metadata.Timestamp);
        PositionSourceText = e.Source switch
        {
            PositionSource.Spawn => "Spawn / zone-in (ProcessAddPlayer)",
            PositionSource.Movement => "Movement / teleport (ProcessNewPosition)",
            _ => e.Source.ToString(),
        };
        RefreshArea();
    }

    private void OnAreaChanged(AreaChanged e) => RefreshArea();

    private void OnPinAdded(MapPinAdded e) => PinsObservedAtText = FormatTimestamp(e.Metadata.Timestamp);

    private void OnPinRemoved(MapPinRemoved e) => PinsObservedAtText = FormatTimestamp(e.Metadata.Timestamp);

    private void OnCelestial(CelestialInfoChanged e)
    {
        HasMoonPhase = true;
        MoonPhaseText = e.DisplayName;
        MoonPhaseRawText = e.Phase == MoonPhase.Unknown
            ? $"{e.RawPhase} (unrecognised token)"
            : e.RawPhase;
        MoonMeasuredAtText = FormatTimestamp(e.Metadata.Timestamp);
    }

    private void OnWeather(WeatherChanged e)
    {
        HasWeather = e.Current is not null;
        WeatherConditionText = e.Current ?? "(weather unknown for this map)";
        WeatherObservedAtText = FormatTimestamp(e.Metadata.Timestamp);
    }

    [RelayCommand]
    private void Refresh() => RefreshArea();

    private void RefreshArea()
    {
        var key = _area.CurrentArea;
        if (string.IsNullOrEmpty(key))
        {
            AreaKey = "(none)";
            AreaFriendlyName = "(not in a game area)";
            AreaShortName = "";
            AreaResolved = false;
            return;
        }

        AreaKey = key;
        if (_refData is not null && _refData.Areas.TryGetValue(key, out var entry))
        {
            AreaFriendlyName = entry.FriendlyName;
            AreaShortName = string.Equals(entry.ShortFriendlyName, entry.FriendlyName, StringComparison.Ordinal)
                ? ""
                : entry.ShortFriendlyName;
            AreaResolved = true;
        }
        else
        {
            AreaFriendlyName = key;
            AreaShortName = "";
            AreaResolved = false;
        }
    }

    public void Dispose()
    {
        _pinPresenter.Pins.CollectionChanged -= _pinsChangedHandler;
        _positionSub?.Dispose(); _positionSub = null;
        _areaSub?.Dispose(); _areaSub = null;
        _pinAddedSub?.Dispose(); _pinAddedSub = null;
        _pinRemovedSub?.Dispose(); _pinRemovedSub = null;
        _celestialSub?.Dispose(); _celestialSub = null;
        _weatherSub?.Dispose(); _weatherSub = null;
    }

    private static string FormatPosition(double x, double y, double z) =>
        string.Format(CultureInfo.InvariantCulture, "X {0:0.00}   Y {1:0.00}   Z {2:0.00}", x, y, z);

    private static string FormatTimestamp(DateTimeOffset? ts) =>
        ts?.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) ?? "—";
}
```

Notes:
- The unused `_pinState` field is kept (injected) because it's needed for any future presenter-bypass read; suppress IDE0052 if it surfaces, or change to `_ = pins;` in the ctor and remove the field. Recommended: keep the field for read-on-demand parity with the other state interfaces.
- `Pins`, `PinCount`, `HasPins` are now thin pass-throughs over the presenter; `CollectionChanged` wired to flip dependent change-notifications.
- `OnPinAdded` / `OnPinRemoved` no longer call `RefreshPins` — the presenter handles its own collection. They only update the observed-at timestamp.

- [ ] **Step 2: Build (test project will still fail until T11)**

Run:
```
dotnet build src\Palantir.Module\Palantir.Module.csproj
```
Expected: build PASSES for the production project. The test project (`Palantir.Tests`) will fail on the next build because the constructor signature changed; T11 fixes that.

---

## Task 9: XAML — bind pin list to the presenter's collection

**Files:**
- Modify: `src/Palantir.Module/Views/WorldStateView.xaml`

Spec §"Approach #4".

- [ ] **Step 1: Open the file and locate the pin `ItemsControl`**

Run: open `src\Palantir.Module\Views\WorldStateView.xaml` in your editor. Find the existing `<ItemsControl ... ItemsSource="{Binding Pins}">` block (it currently binds to the VM's `ObservableCollection<MapPinRow>`).

- [ ] **Step 2: Replace its `DataTemplate` with inline `MapPinEntry` formatting**

The bound items are now `MapPinEntry` records (`X`, `Z`, `Label`, `Shape`, `Color`). Replace the template body so it formats the entry inline. Use `MultiBinding`/`StringFormat` where it reads better; otherwise the simplest shape:

```xml
<ItemsControl ItemsSource="{Binding Pins}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <StackPanel Orientation="Horizontal" Margin="0,2">
                <TextBlock Text="{Binding Label, FallbackValue='Unnamed pin', TargetNullValue='Unnamed pin'}"
                           FontWeight="SemiBold" Margin="0,0,8,0"/>
                <TextBlock>
                    <Run Text="X "/>
                    <Run Text="{Binding X, StringFormat={}{0:0.00}, Mode=OneWay}"/>
                    <Run Text="   Z "/>
                    <Run Text="{Binding Z, StringFormat={}{0:0.00}, Mode=OneWay}"/>
                </TextBlock>
                <TextBlock Margin="8,0,0,0" Opacity="0.6">
                    <Run Text="Color "/>
                    <Run Text="{Binding Color, Mode=OneWay}"/>
                    <Run Text=" · Shape "/>
                    <Run Text="{Binding Shape, Mode=OneWay}"/>
                </TextBlock>
            </StackPanel>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

If the pre-existing template used `Appearance` and `Detail` text strings, drop those bindings — the inline `Color`/`Shape` runs above replace them. Verify against [docs/wpf-gotchas.md](../../wpf-gotchas.md) §"`<Run>` bindings" since `<Run>` requires `Mode=OneWay` explicitly when bound to non-string properties (it's already set above).

- [ ] **Step 3: Build the solution**

Run:
```
dotnet build Mithril.slnx
```
Expected: build PASSES. (The test project still has compile errors from T8; T11 finishes that.)

---

## Task 10: DI — register `IUiEventSubscriber` + `WpfMapPinPresenter`

**Files:**
- Modify: `src/Palantir.Module/PalantirModule.cs`

Spec §"Approach #4".

- [ ] **Step 1: Add the two singleton registrations**

In `PalantirModule.Register`, append (above the existing `services.AddSingleton<PalantirShellViewModel>();` line):

```csharp
        services.AddSingleton<Arda.Wpf.IUiEventSubscriber>(sp => new Arda.Wpf.WpfUiEventSubscriber(
            sp.GetRequiredService<Arda.Contracts.IDomainEventSubscriber>(),
            System.Windows.Application.Current.Dispatcher,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Arda.Wpf.WpfUiEventSubscriber>>()));
        services.AddSingleton<Arda.Wpf.WpfMapPinPresenter>();
```

Lifetime rationale: `IUiEventSubscriber` is a thin singleton that captures one `Dispatcher` (the shell's UI thread). `WpfMapPinPresenter` is a singleton because there's one UI thread and one canonical pin collection; multiple consumers may bind safely.

Decision: Palantir-local registration for now. When a second consumer arrives, promote to an `AddArdaWpf()` extension method in `Arda.Wpf` and call from shell DI. Recorded in spec §"Out of scope".

- [ ] **Step 2: Build the full solution**

Run:
```
dotnet build Mithril.slnx
```
Expected: build PASSES for production (`Palantir.Module`, `Mithril.Shell`); test project still failing on `WorldStateViewModelTests` ctor signature. T11 fixes.

---

## Task 11: Update `WorldStateViewModelTests` to the new ctor + presenter

**Files:**
- Modify: `tests/Palantir.Tests/WorldStateViewModelTests.cs`

Spec §"Approach #4" + Testing.

- [ ] **Step 1: Update the `NewVm` helper**

Find the existing `NewVm` helper at the bottom of the test class. Replace it with:

```csharp
    private static WorldStateViewModel NewVm(
        out FakeBus bus,
        IPositionState? position = null,
        IAreaState? area = null,
        IMapPinState? pins = null,
        ICelestialState? celestial = null,
        IWeatherState? weather = null,
        IReferenceDataService? refData = null)
    {
        bus = new FakeBus();
        var pinState = pins ?? new FakeMapPinState();
        var subscriber = new Arda.Wpf.SyncUiEventSubscriber(bus);
        var presenter = new Arda.Wpf.WpfMapPinPresenter(pinState, subscriber);

        return new WorldStateViewModel(
            position ?? new FakePositionState(),
            area ?? new FakeAreaState(),
            pinState,
            celestial ?? new FakeCelestialState(),
            weather ?? new FakeWeatherState(),
            subscriber,
            presenter,
            refData);
    }
```

If existing test fakes (`FakeBus`, `FakePositionState`, etc.) live in a sibling support file, leave them be; otherwise add `FakeMapPinState` matching the WpfMapPinPresenterTests one:

```csharp
    private sealed class FakeMapPinState : IMapPinState
    {
        private readonly List<MapPinEntry> _pins;
        public FakeMapPinState(params MapPinEntry[] seed) => _pins = new(seed);
        public IReadOnlyCollection<MapPinEntry> Pins => _pins.ToArray();
    }
```

The pre-existing `FakeBus` must implement both `IDomainEventPublisher` and `IDomainEventSubscriber` so the test can both `bus.Publish(...)` and pass it through `SyncUiEventSubscriber`. If `FakeBus` only implemented subscribe today, add the publish side using the same shape as `WpfMapPinPresenterTests.TestBus` (T6).

- [ ] **Step 2: Update any pin-related assertions**

Find every test that asserts on `vm.Pins` (the now-pass-through collection). Most will still work — `Pins` still exists as a property; its element type changed from `MapPinRow` to `MapPinEntry`. Any test that pattern-matched `MapPinRow` properties (`Label`, `Appearance`, `Coords`, `Detail`) needs to read off `MapPinEntry` (`Label`, `Shape`, `Color`, `X`, `Z`).

If a test asserted `vm.PinCount` / `vm.HasPins`, those still exist (now pass-through). No edit needed.

- [ ] **Step 3: Run the Palantir test suite**

Run:
```
dotnet test tests\Palantir.Tests\Palantir.Tests.csproj -v normal
```
Expected: all tests PASS. If a pin-shape assertion fails, mechanically update it to read `MapPinEntry` fields rather than the old `MapPinRow` fields; the underlying observed values are unchanged.

- [ ] **Step 4: Run the full solution test suite**

Run:
```
dotnet test Mithril.slnx
```
Expected: all tests PASS — including `Arda.World.Player.Tests` (snapshot test + existing) and `Arda.Wpf.Tests` (UiEventSubscriber, WpfMapPinPresenter, InventoryProjection).

- [ ] **Step 5: Commit (commit B3)**

```
git add src/Palantir.Module/ViewModels/WorldStateViewModel.cs \
        src/Palantir.Module/Views/WorldStateView.xaml \
        src/Palantir.Module/PalantirModule.cs \
        tests/Palantir.Tests/WorldStateViewModelTests.cs
git commit -m "$(cat <<'EOF'
refactor(palantir): WorldStateViewModel consumes IUiEventSubscriber + WpfMapPinPresenter

Eliminates the local Action<Action> _dispatch field + DefaultDispatch
static + the per-handler InvokeAsync wrapper that was the actual crash
vector (its unobserved DispatcherOperation Task captured handler
exceptions and the finalizer rethrew them as AggregateException).

The underlying crash was already fixed by PR-A (MapPins.Pins snapshot).
This PR is the consumer-side cleanup: pin rendering binds directly to
WpfMapPinPresenter.Pins (ObservableCollection<MapPinEntry> on the UI
thread, incrementally mutated by delta events). The VM keeps a thin
Pins / PinCount / HasPins pass-through for binding compatibility plus
the PinsObservedAtText timestamp it always owned.

XAML pin DataTemplate formats MapPinEntry inline (Label / X / Z / Color
/ Shape via <Run> bindings) — the per-row MapPinRow projection record
is gone.

Tests use SyncUiEventSubscriber (the test seam from commit B1) so they
run inline without an STA dispatcher. Determinism unchanged from the
test's point of view.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Manual verify + open PR-B + post-merge INDEX flip

**Files:**
- Modify: `docs/planning/INDEX.md` (status flip post-merge)

Spec Acceptance #4 + #7.

- [ ] **Step 1: Run the shell with the game open**

Run:
```
dotnet run --project src\Mithril.Shell
```

With Project Gorgon also running and logged in:

1. Switch to Palantir → World State tab.
2. In-game, open the map and rapidly add 5+ pins in quick succession (right-click → Add Pin).
3. Rapidly remove some of those pins (right-click pin → Remove).
4. Switch areas (use a portal or teleport) twice in a row.

Confirm:
- No crash dialog. Process stays alive.
- Pin list updates incrementally — visible row count grows from N to N+5 with no intermediate empty state / flicker.
- Area transition clears the pin list cleanly; the new area's pins repopulate as the login/area-entry replay burst arrives.
- The `boot.log` / diagnostics ring buffer shows zero `LogLevel.Error` from `WpfUiEventSubscriber` (no handler exceptions during normal operation).

If a crash dialog DOES surface, capture the stack trace and abort the commit — investigate before proceeding.

- [ ] **Step 2: Push branch and open PR-B**

```
git push -u origin refactor/arda-wpf-ui-dispatch
gh pr create \
  --title "refactor(arda.wpf): IUiEventSubscriber + WpfMapPinPresenter; migrate Palantir" \
  --body-file - <<'EOF'
## Summary

Follow-on to merged PR-A (`MapPins.Pins` snapshot). The crash is already fixed on `main`; this PR is the **consumer-side cleanup** that prevents the bug class from re-appearing in any future WPF consumer.

- **New `Arda.Wpf` primitives** (sit at the consumer edge, after `bus.Publish` returns — Arda's producer-side determinism is unchanged):
  - `IUiEventSubscriber` + `WpfUiEventSubscriber` impl with `SafeInvoke` try/catch + `ILogger` — UI-affinitised, crash-safe wrapper over `IDomainEventSubscriber`. A handler that throws logs instead of crashing via the finalizer thread.
  - `SyncUiEventSubscriber` — synchronous test seam for VMs that previously took an `Action<Action>` sync dispatcher.
  - `WpfMapPinPresenter` — sister to `InventoryProjection`. `ObservableCollection<MapPinEntry>` on the UI thread, coord-keyed dedup (0.01 tolerance matching `MapPins.OnRemove`), area-change clear.
- **Palantir migration.** `WorldStateViewModel` consumes both new primitives, deletes its `_dispatch` field + `DefaultDispatch` static + `RefreshPins` + parallel pin collection. XAML pin DataTemplate formats `MapPinEntry` inline via `<Run>` bindings.
- **Non-WPF consumers are deliberately unchanged.** Legolas coordinators, L4 composers, replay tests, and the world-sim driver continue to use `IDomainEventSubscriber` directly — they need sync-on-Arda-thread firing for their lock-based coordination, and `Arda.Wpf` is not on their reference graph.

## Reviewer focus

Two questions:

1. **Is `IUiEventSubscriber` the right shape?** It's `IDomainEventSubscriber` with one extra guarantee (handlers run on the UI thread, inside a `SafeInvoke` try/catch). Test coverage on `SafeInvoke` directly + `SyncUiEventSubscriber` round-trip covers both contracts; `WpfUiEventSubscriber` is a 5-line composition that's exercised end-to-end by the Palantir manual repro.
2. **Is the Palantir migration correct?** `WorldStateViewModel` becomes meaningfully smaller — see the new file in commit B3. Pin display now lives in the presenter; the VM's `Pins`/`PinCount`/`HasPins` are thin pass-throughs, with `CollectionChanged` wired through a stored handler field (so `Dispose` unsubscribes cleanly — the presenter outlives the VM as a singleton).

## Test plan

- [x] `dotnet build Mithril.slnx` is 0 warnings / 0 errors.
- [x] `dotnet test Mithril.slnx` all green (new `UiEventSubscriberTests` + `WpfMapPinPresenterTests`; refactored `WorldStateViewModelTests` using `SyncUiEventSubscriber`).
- [x] Manual: pin list updates incrementally (no clear-and-refill flicker); area transitions clear cleanly; no `LogLevel.Error` from `WpfUiEventSubscriber` during normal operation.
- [x] Determinism check: `Arda.World.Player.Tests` + `Arda.Composition.Tests` pass unchanged; no new ProjectReference from any non-`Arda.Wpf` project onto `Arda.Wpf`; world-sim driver (if pin-aware) unchanged.

Design + plan: [`docs/planning/arda-state-snapshot-and-ui-dispatch/`](../tree/refactor/arda-wpf-ui-dispatch/docs/planning/arda-state-snapshot-and-ui-dispatch).

Closes #NNN-B.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Replace `NNN-B` with Issue-B's number.

- [ ] **Step 3: Get PR-B merged**

Wait for CI + review. Address review feedback if any. Once merged: proceed to Step 4.

- [ ] **Step 4: Flip INDEX status to `shipped` (post-merge)**

```
git switch main
git pull --ff-only
git switch -c docs/index-status-flip-arda-state-snapshot
```

Edit `docs/planning/INDEX.md`. Change the `arda-state-snapshot-and-ui-dispatch` row's status from `active` to `shipped`; update the Issue/PR column to link both issues (Issue-A · Issue-B):

```markdown
| [arda-state-snapshot-and-ui-dispatch](arda-state-snapshot-and-ui-dispatch/) | shipped | [#NNN-A](https://github.com/moumantai-gg/mithril/issues/NNN-A) · [#NNN-B](https://github.com/moumantai-gg/mithril/issues/NNN-B) | Fix Palantir pin-enum crash; introduce Arda.Wpf IUiEventSubscriber + WpfMapPinPresenter |
```

- [ ] **Step 5: Commit (commit B4) + open the docs-only PR**

```
git add docs/planning/INDEX.md
git commit -m "$(cat <<'EOF'
docs(planning): flip arda-state-snapshot-and-ui-dispatch to shipped

Both PR-A (#NNN-A) and PR-B (#NNN-B) have merged. Mark the slug shipped
in INDEX; spec + plan stay as living history per docs/planning/INDEX.md
convention.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
git push -u origin docs/index-status-flip-arda-state-snapshot
gh pr create \
  --title "docs(planning): flip arda-state-snapshot-and-ui-dispatch to shipped" \
  --body "Post-merge status flip; trivial docs-only change."
```

This is a tiny docs PR (one line in INDEX.md) — the squash-merge-orphan rule (`squash_merge_orphans_netzero_plans`) doesn't apply since we're flipping a status, not adding-then-deleting. Merge after CI passes.

---

## Self-review checklist

- **Spec coverage:**
  - **PR-A** — Snapshot fix → T1, T2; PR open/merge → T2.5 ✓
  - **PR-B** — `IUiEventSubscriber` interface + WPF impl + Sync test double → T3, T5 ✓ (T4 is a sanity-build, no new code); `WpfMapPinPresenter` → T6, T7 ✓; Palantir VM migration (including XAML + DI) → T8, T9, T10 ✓; Tests refactored → T11 ✓; Manual verify + PR open/merge + INDEX flip → T12 ✓
  - Determinism preservation → verified by spec §"Why the chosen approach preserves determinism" + tested via untouched `Arda.World.Player.Tests` suite in T2 (PR-A) and T11 step 4 (PR-B) ✓
- **PR boundary integrity:**
  - PR-A ships standalone: build + tests green at end of T2; no dependency on any PR-B file. ✓
  - PR-B's WpfMapPinPresenter seed (T7) relies on PR-A's snapshot semantics; the inline comment makes this explicit. ✓
  - PR-B's branch is created off `main` (T0B Step 1: `git switch main; git pull --ff-only; git switch -c …`), guaranteeing PR-A is in its base. ✓
  - No "stacked PR" requirement — each PR is reviewable against a known-stable main. ✓
- **Reviewer-focus quality:**
  - PR-A's body asks one load-bearing question (determinism); answer is one sentence. ✓
  - PR-B's body asks two scoped questions (abstraction shape, migration correctness); each is independently auditable in one commit. ✓
- **Placeholder scan:** no "TBD" / "implement later" / "similar to" / "add appropriate error handling" — all code shown. The `NNN-A` / `NNN-B` issue-number placeholders are intentional (filed in T0A / T0B then substituted into PR bodies in T2.5 / T12).
- **Type consistency:** `IUiEventSubscriber.Subscribe<T>(Action<T>) where T : struct` matches `IDomainEventSubscriber` exactly. `WpfMapPinPresenter(IMapPinState, IUiEventSubscriber)` signature consistent across T6 (tests) and T7 (impl). `WorldStateViewModel` ctor signature consistent T8 (impl) / T10 (DI factory) / T11 (test helper). `MapPinEntry` field shape (`X`, `Z`, `Label`, `Shape`, `Color`) consistent in all touch-points. Commit labels (`A1`, `A2`, `B1`, `B2`, `B3`, `B4`) consistent between Commit map and the per-task commit steps.
