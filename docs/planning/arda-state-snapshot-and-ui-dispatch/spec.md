# Arda map-pin snapshot + `Arda.Wpf` UI-dispatch primitives

**Tracked in:** two issues, one per PR — see [plan.md §"PR organisation"](plan.md#pr-organisation--two-prs-for-review-efficiency) for the split. PR-A is the source-side snapshot fix (`type:bug`); PR-B is the consumer-side cleanup (`type:refactor`).

## Context

### The crash

Mithril crashed on the user's machine with this dialog:

```
System.AggregateException: A Task's exception(s) were not observed either by Waiting
on the Task or accessing its Exception property. As a result, the unobserved
exception was rethrown by the finalizer thread.
(Collection was modified; enumeration operation may not execute.)
 ---> System.InvalidOperationException: Collection was modified; enumeration
      operation may not execute.
   at System.Collections.Generic.List`1.Enumerator.MoveNext()
   at Palantir.ViewModels.WorldStateViewModel.RefreshPins()
   at Palantir.ViewModels.WorldStateViewModel.<>c__DisplayClass39_0.<OnPinAdded>b__0()
   at System.Windows.Threading.DispatcherOperation.InvokeDelegateCore()
   at System.Windows.Threading.DispatcherOperation.InvokeImpl()
```

### Root cause

[`Arda.World.Player.Internal.MapPins`](../../../src/Arda/Arda.World.Player/Internal/MapPins.cs#L25-L27)
holds `private readonly List<MapPinEntry> _pins = []` and **exposes the live
list verbatim**:

```csharp
public IReadOnlyCollection<MapPinEntry> Pins => _pins;
internal IReadOnlyList<MapPinEntry> PinsList => _pins;
```

[`MapScope`](../../../src/Arda/Arda.World.Player/Internal/MapScope.cs#L26) re-exposes
the same list via `IMapState.Pins => pins.PinsList`.

`MapPins.OnAdd`/`OnRemove` mutate `_pins` on the **Arda ingest thread**
([lines 42, 56](../../../src/Arda/Arda.World.Player/Internal/MapPins.cs#L42)).
[`WorldStateViewModel.RefreshPins`](../../../src/Palantir.Module/ViewModels/WorldStateViewModel.cs#L227-L234)
enumerates the same list on the **WPF UI thread** via
`foreach (var pin in _pinState.Pins)`. There is no lock, snapshot, or
immutable swap protecting the cross-thread read.

The race:

1. Arda thread: `ProcessMapPinAdd` → `_pins.Add(...)` → `_bus.Publish(MapPinAdded)`.
2. `WorldStateViewModel.OnPinAdded` runs synchronously on the Arda thread,
   calls `_dispatch(() => RefreshPins())` →
   [`DefaultDispatch`](../../../src/Palantir.Module/ViewModels/WorldStateViewModel.cs#L258-L263)
   → `Dispatcher.InvokeAsync(action)`.
3. Arda thread keeps draining log lines; another `OnAdd`/`OnRemove` mutates
   `_pins`.
4. UI thread picks up the queued op and starts
   `foreach (var pin in _pinState.Pins)`. `List<T>.Enumerator`'s version
   check fires → `InvalidOperationException`.

The dispatched action's `Task` captures the exception, nobody observes it,
the finalizer reraises as `AggregateException` — the dialog above.

### Survey — the bug is not Palantir-local

| Module | Site | Enumerate / read pattern | Off-Arda thread |
|---|---|---|---|
| Palantir | [`WorldStateViewModel.RefreshPins`](../../../src/Palantir.Module/ViewModels/WorldStateViewModel.cs#L230) | `foreach` | UI (dispatched) — **crashed** |
| Palantir | [`WorldStateViewModel.SeedFromState`](../../../src/Palantir.Module/ViewModels/WorldStateViewModel.cs#L123) | `foreach` (via RefreshPins) | UI (DI activation) |
| Legolas | [`CharacterPinAnchor` ctor + `ResolveFromPinState`](../../../src/Legolas.Module/Services/CharacterPinAnchor.cs#L65) | `foreach` | DI thread + handler |
| Legolas | [`PinCalibrationCoordinator.SyncExistingPins`](../../../src/Legolas.Module/Services/PinCalibrationCoordinator.cs#L148) (+ `PinsAvailable` + `.Where(...).ToList()`) | enumerate + Count | UI (`disp.Invoke`) + bindings |
| Legolas | [`MotherlodeViewModel`](../../../src/Legolas.Module/ViewModels/MotherlodeViewModel.cs#L157) | aliases the list | caller-dependent |
| Legolas | [`MotherlodeMeasurementCoordinator`](../../../src/Legolas.Module/Services/MotherlodeMeasurementCoordinator.cs#L692) | aliases the list | caller-dependent |

Six sites; Palantir is the loudest because its handler dispatches the tightest
loop. Legolas's reads are dormant landmines.

### Why not "put a UI dispatcher inside Arda"

A natural-feeling fix is for `IDomainEventPublisher` to know about a
`SynchronizationContext` / WPF Dispatcher and post subscribers through it.
**Rejected**, because:

1. **Sync-on-Arda-thread subscribers depend on synchronous firing.**
   `CharacterPinAnchor` (`lock(_gate)` + read-current-state), the L4
   composers, and the Arda replay tests all assume that when `bus.Publish(evt)`
   returns, the subscribers have seen `evt` and the state mutation is visible.
   Posting them through a dispatcher loses that guarantee.
2. **Replay tests would have to drain a dispatcher queue** at end-of-corpus
   before asserting final state — coupling the deterministic Arda harness to
   WPF.
3. **The world-sim driver and headless tests would inherit a WPF dependency**
   (or have to thread a null-dispatcher config through the pipeline). That
   leaks an architectural property out of `Arda.Wpf` into `Arda` itself.

Arda's "publish synchronously on the ingest thread" is producer-side
determinism — the property that makes [`IPlayerLogStream` replay-before-live](../../.claude/memory/playerlogstream_replay_drain_late_subscriber.md)
work and the world-sim migration (#601) feasible. The fix must preserve it.

### Why the chosen approach preserves determinism

Arda's determinism is a property of the **producer**: same input log lines
→ same internal state evolution → same sequence of published events. This
spec changes neither.

- **`MapPins.Pins` snapshot-on-read** affects only the read path. Mutation is
  unchanged (still single-threaded on the ingest thread). The snapshot is a
  consistent point-in-time view; ingest thread continues at full speed.
- **`IUiEventSubscriber`** is a *consumer-edge* wrapper. It sits **after**
  `bus.Publish` returns. Non-UI subscribers (Legolas coordinators, replay
  tests, world-sim driver) keep using `IDomainEventSubscriber` and see
  identical timing.
- Headless contexts (`Arda.World.Player.Tests`, world-sim) do not reference
  `Arda.Wpf` at all, so `IUiEventSubscriber` does not exist for them.
  Determinism there is structurally untouchable.
- WPF VM tests inject a **synchronous `IUiEventSubscriber` test double** that
  runs handlers inline — equivalent (in fact strictly stronger) determinism
  than the current `Action<Action>` sync dispatcher pattern.

## Approach

Four pieces, each minimal and independently testable.

### 1. Snapshot at the source

[`MapPins`](../../../src/Arda/Arda.World.Player/Internal/MapPins.cs):

```csharp
// Before:
public IReadOnlyCollection<MapPinEntry> Pins => _pins;
internal IReadOnlyList<MapPinEntry> PinsList => _pins;

// After:
public IReadOnlyCollection<MapPinEntry> Pins => _pins.ToArray();
internal IReadOnlyList<MapPinEntry> PinsList => _pins.ToArray();
```

The mutator path is untouched (single-threaded ingest). `ToArray()` over a
List of ~handful-of-pins is a few-byte allocation per read; pin reads are
human-paced. Counted/snapshotted callers (`PinCalibrationCoordinator.PinsAvailable`,
the `.Where(...).ToList()` projection) keep working unchanged.

[`MapScope`](../../../src/Arda/Arda.World.Player/Internal/MapScope.cs#L26):
no code change — `pins.PinsList` is now an array snapshot.

**Why `ToArray` and not `ImmutableList<T>` / volatile-array swap.** Pin
counts are tiny (per-area handful; see the existing
`RefreshPins` comment "the set is tiny (a handful per area)"). Pin reads
are infrequent (event-driven). Allocation cost is negligible; code clarity
beats premature optimisation. If pin volumes ever change radically (they
won't — PG's pin verb set is fixed), swap to immutable-list-on-mutate
later. Don't over-engineer now.

### 2. `IUiEventSubscriber` in `Arda.Wpf`

New abstraction co-located with [`InventoryProjection`](../../../src/Arda/Arda.Wpf/InventoryProjection.cs) —
`Arda.Wpf` is already the designated "Arda → WPF bridge" project and
references `Arda.Contracts`.

```csharp
// src/Arda/Arda.Wpf/UiEventSubscriber.cs
using System.Windows.Threading;
using Arda.Contracts;
using Microsoft.Extensions.Logging;

namespace Arda.Wpf;

/// <summary>
/// UI-thread-affined wrapper over <see cref="IDomainEventSubscriber"/>.
/// Subscribers receive their handlers on the WPF Dispatcher thread,
/// inside a try/catch that logs (rather than crashing via the finalizer
/// thread when the underlying DispatcherOperation's Task is unobserved).
///
/// <para>Non-UI subscribers should continue to depend on
/// <see cref="IDomainEventSubscriber"/> directly — their lock-based
/// coordination relies on synchronous handler firing on the Arda
/// ingest thread.</para>
/// </summary>
public interface IUiEventSubscriber
{
    IDisposable Subscribe<T>(Action<T> handler) where T : struct;
}

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
        => _inner.Subscribe<T>(evt => _dispatcher.InvokeAsync(() =>
        {
            try { handler(evt); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UI handler for {Event} threw on the dispatcher; suppressed " +
                    "to prevent finalizer-thread crash.",
                    typeof(T).Name);
            }
        }));
}
```

Plus a `SyncUiEventSubscriber` test double in `tests/Arda.Wpf.Tests` (or
inside the production project as `internal` with `InternalsVisibleTo`):

```csharp
internal sealed class SyncUiEventSubscriber(IDomainEventSubscriber inner)
    : IUiEventSubscriber
{
    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        => inner.Subscribe<T>(handler);
}
```

`SyncUiEventSubscriber` runs handlers on whatever thread `bus.Publish`
is called from (typically the test thread) — exact replacement for the
current `Action<Action> _dispatch` sync test pattern.

### 3. `WpfMapPinPresenter` in `Arda.Wpf`

Sister to `InventoryProjection`. Owns an `ObservableCollection<MapPinEntry>`
on the UI thread; consumers bind directly.

```csharp
// src/Arda/Arda.Wpf/WpfMapPinPresenter.cs
using System.Collections.ObjectModel;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;

namespace Arda.Wpf;

/// <summary>
/// UI-thread projection of <see cref="IMapPinState"/>. Subscribes via
/// <see cref="IUiEventSubscriber"/> so every collection mutation happens
/// on the WPF Dispatcher thread, eliminating the cross-thread enumeration
/// race against the Arda ingest thread.
///
/// Pin identity is the rounded (X, Z) coordinate, matching
/// <see cref="MapPins"/>'s remove-by-coord semantics.
/// </summary>
public sealed class WpfMapPinPresenter : IDisposable
{
    private readonly Dictionary<(long X, long Z), MapPinEntry> _byCoord = new();
    private readonly IDisposable _addedSub;
    private readonly IDisposable _removedSub;
    private readonly IDisposable _areaSub;

    public ObservableCollection<MapPinEntry> Pins { get; } = [];

    public WpfMapPinPresenter(IMapPinState state, IUiEventSubscriber bus)
    {
        // Seed first — IMapPinState.Pins is a snapshot per the Arda fix above,
        // so this is safe regardless of which thread we're on. Events that
        // land between seed and subscribe will be applied as deltas; the
        // coord-keyed Dictionary upserts so a doubled Add is a no-op.
        foreach (var p in state.Pins) Upsert(p);
        SyncCollection();

        _addedSub = bus.Subscribe<MapPinAdded>(OnAdded);
        _removedSub = bus.Subscribe<MapPinRemoved>(OnRemoved);
        _areaSub = bus.Subscribe<AreaChanged>(OnAreaChanged);
    }

    private static (long X, long Z) Key(double x, double z)
        => ((long)Math.Round(x * 100), (long)Math.Round(z * 100));
    //         ^ matches MapPins.OnRemove's "Math.Abs(< 0.01)" coord-key tolerance.

    private void Upsert(MapPinEntry pin)
        => _byCoord[Key(pin.X, pin.Z)] = pin;

    private void OnAdded(MapPinAdded e)
    {
        var entry = new MapPinEntry(e.X, e.Z, e.Label, e.Shape, e.Color);
        var key = Key(e.X, e.Z);
        var existed = _byCoord.ContainsKey(key);
        _byCoord[key] = entry;
        if (existed)
        {
            // Replace in the OC at its existing index so binding clients see
            // a single mutation, not a remove+add flicker.
            for (var i = 0; i < Pins.Count; i++)
            {
                if (Key(Pins[i].X, Pins[i].Z) == key) { Pins[i] = entry; return; }
            }
        }
        Pins.Add(entry);
    }

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
        // Per StateResetHandler: map pins are per-area; on area transition
        // MapPins.Reset() clears the underlying list. Mirror that here.
        _byCoord.Clear();
        Pins.Clear();
    }

    private void SyncCollection()
    {
        foreach (var p in _byCoord.Values) Pins.Add(p);
    }

    public void Dispose()
    {
        _addedSub.Dispose();
        _removedSub.Dispose();
        _areaSub.Dispose();
    }
}
```

DI lifetime: **singleton**. One WPF UI thread → one `ObservableCollection`;
multiple consumers may bind to the same instance safely.

### 4. Palantir `WorldStateViewModel` migration

- Inject `IUiEventSubscriber` (replaces `IDomainEventSubscriber` + `_dispatch` field).
- Inject `WpfMapPinPresenter`.
- Drop `RefreshPins` entirely. `OnPinAdded`/`OnPinRemoved` shrink to "update the
  observed-at timestamp" — pin rendering is the presenter's job.
- Keep the `Pins` / `PinCount` / `HasPins` properties as thin pass-throughs over
  the presenter (`Pins => _pinPresenter.Pins` etc.) — XAML bindings stay valid
  without rewriting them, and `MapPinEntry`'s fields format inline via `<Run>`
  bindings in the pin DataTemplate. Pin-count change notification is wired
  through the presenter's `CollectionChanged`, with the handler stored in a
  field so `Dispose` can unsubscribe (the presenter is a singleton — leaving
  the subscription live would pin the VM in memory).
- The other five handlers (`OnPosition`, `OnAreaChanged`, `OnCelestial`,
  `OnWeather`, `Refresh`) become plain methods — no `_dispatch(...)` wrapper.
  They run on the UI thread automatically via `IUiEventSubscriber`.

Resulting VM is meaningfully smaller (deletes the `DefaultDispatch` static, the
`_dispatch` field, the `Action<Action>?` test ctor parameter, the parallel
`ObservableCollection<MapPinRow>`, the `MapPinRow` projection record, and
`RefreshPins`).

## Files to modify

### Created

| Path | Purpose |
|---|---|
| `src/Arda/Arda.Wpf/UiEventSubscriber.cs` | `IUiEventSubscriber` interface + `WpfUiEventSubscriber` impl |
| `src/Arda/Arda.Wpf/SyncUiEventSubscriber.cs` (or `internal` in `UiEventSubscriber.cs`) | Synchronous test double |
| `src/Arda/Arda.Wpf/WpfMapPinPresenter.cs` | Bindable pin projection |
| `tests/Arda.Wpf.Tests/Arda.Wpf.Tests.csproj` (if absent) | Test project for `Arda.Wpf` |
| `tests/Arda.Wpf.Tests/UiEventSubscriberTests.cs` | Sync-wrapper round-trip + exception-swallow tests |
| `tests/Arda.Wpf.Tests/WpfMapPinPresenterTests.cs` | Seed + add/remove/area-change + upsert/dedup tests |

### Modified

| Path | Change |
|---|---|
| `src/Arda/Arda.World.Player/Internal/MapPins.cs` | `Pins` / `PinsList` return `_pins.ToArray()` |
| `src/Arda/Arda.Wpf/Arda.Wpf.csproj` | Add `Microsoft.Extensions.Logging.Abstractions` PackageReference (for `ILogger<T>`). No new ProjectReference needed — `MapPinEntry`, `IMapPinState`, and the pin/area events all live in the already-referenced `Arda.Contracts` (namespace `Arda.World.Player`). |
| `src/Palantir.Module/ViewModels/WorldStateViewModel.cs` | Consume `IUiEventSubscriber` + `WpfMapPinPresenter`; delete `_dispatch`, `RefreshPins`, `Pins`, `DefaultDispatch` |
| `src/Palantir.Module/Views/WorldStateView.xaml` | Pin section binds to `Presenter.Pins` (or wraps `MapPinEntry` → display via converter) |
| `src/Palantir.Module/PalantirModule.cs` | Register `IUiEventSubscriber`, `WpfMapPinPresenter` (singletons) |
| `tests/Palantir.Tests/WorldStateViewModelTests.cs` | Use `SyncUiEventSubscriber`; assertions over `presenter.Pins` instead of `vm.Pins` |
| `tests/Palantir.Tests/Palantir.Tests.csproj` | ProjectReference to `Arda.Wpf` for `SyncUiEventSubscriber` (or test-local copy) |

### Verified not affected (regression-only)

| Path | Why unchanged |
|---|---|
| `src/Legolas.Module/Services/CharacterPinAnchor.cs` | Reads `_mapPinState.Pins` from DI ctor + Arda-thread handlers; the Arda snapshot fix protects both. Lock-based coordination unchanged. |
| `src/Legolas.Module/Services/PinCalibrationCoordinator.cs` | `SyncExistingPins`, `PinsAvailable`, `.Where(...).ToList()` all read `_pinState.Pins` from UI thread or Arda thread; both now safe. |
| `src/Legolas.Module/ViewModels/MotherlodeViewModel.cs` | Aliases `_pinState.Pins`; downstream callers enumerate a snapshot. |
| `src/Legolas.Module/Services/MotherlodeMeasurementCoordinator.cs` | Same. |
| `src/Arda/Arda.World.Player/Internal/StateResetHandler.cs` | `_mapPins.Reset()` unchanged; presenter learns to clear via `AreaChanged` subscription. |
| `src/Arda/Arda.Wpf/InventoryProjection.cs` | Different state-change shape (`StateChanged` event, not Arda event types). Out of scope; see "Out of scope" below. |

## Testing

TDD. Tests pin the determinism and bug-resistance properties — write them
before the implementation.

### `Arda.World.Player.Tests` — MapPins snapshot

- New test: `Pins_ReturnedCollection_IsSnapshot_NotLiveView`. After capturing
  the returned collection, an `OnAdd` on the underlying list does **not**
  appear in the captured snapshot.
- Existing tests should pass unchanged (verb dispatch, replay, area reset).

### `Arda.Wpf.Tests` — `UiEventSubscriber`

- `SyncUiEventSubscriber` (test double): subscribed handler receives the
  exact event payload, on the publishing thread.
- `WpfUiEventSubscriber` against an in-process WPF Dispatcher (`Dispatcher.CurrentDispatcher`
  + STA test fixture; or a dispatcher-frame helper): handler runs on the
  dispatcher thread.
- `WpfUiEventSubscriber` exception-swallow: a handler that throws does NOT
  surface to the publisher; an `ILogger` receives `LogLevel.Error` with the
  exception attached. Verify no `AggregateException` reaches the finalizer
  thread (use `TaskScheduler.UnobservedTaskException` recorder).

### `Arda.Wpf.Tests` — `WpfMapPinPresenter`

- Seed-from-state: ctor reads `IMapPinState.Pins`, populates `ObservableCollection`.
- `MapPinAdded` upserts new coord (new row), replaces existing coord (in-place).
- `MapPinRemoved` removes by coord; missing-coord is a no-op.
- `AreaChanged` clears both internal index and OC.
- Disposal unsubscribes (no further events affect the OC).
- Coord-key tolerance: floating-point inputs round-trip to the same `(long, long)`
  key (mirrors `MapPins.OnRemove`'s `< 0.01` epsilon).

### `Palantir.Tests` — `WorldStateViewModelTests`

- Refactor `NewVm` helper to construct `SyncUiEventSubscriber(bus)` and
  `WpfMapPinPresenter(state, syncSubscriber)`; pass into VM.
- Pin-related assertions move from `vm.Pins` (gone) to `presenter.Pins`.
- All existing non-pin tests (area, position, weather, celestial) keep passing.
- New test: `OnPosition_DispatchedHandlerThrows_Logs_DoesNotCrash` — handler
  configured to throw; assert `ILogger` recorded the error, no exception
  propagates, subsequent events still process.

### Manual verification (verification owed)

- Launch shell with the user's repro setup; observe rapid pin add/remove via
  in-game pin editor while Palantir's World State tab is open. The original
  crash should not reproduce.
- Pin list updates incrementally (no flicker / full-list rebuild). Add 5 pins
  in quick succession: visible row count grows from N to N+5 without the
  intermediate empty state.
- Area transition (zone into / out of a region with pins): pin list clears
  cleanly; next area's pins populate as the replay burst arrives.

### Determinism regression check

- `Arda.World.Player.Tests` full suite green — proves producer-side
  determinism unchanged.
- World-sim driver (if it has a pin-aware smoke test): unchanged behaviour.
  No new dependency on `Arda.Wpf` from anything in `world-sim/`.

## Out of scope

- **Legolas consumer migration to `WpfMapPinPresenter`.** Legolas's pin
  consumers are coordinators, not XAML lists — they read state on demand
  and apply deltas to their own structures. The Arda snapshot fix protects
  them as-is. If/when a Legolas VM wants live bindable pin display, it
  registers `WpfMapPinPresenter` and binds directly.
- **`InventoryProjection` migration to `IUiEventSubscriber`.** It uses a
  different state-change event shape (`Action`-typed `StateChanged`, not
  Arda's typed events). A subsequent issue can converge it; not bundled
  here.
- **Broader audit of other VMs using `Dispatcher.InvokeAsync` without
  observing the returned task.** File a follow-up issue tagged
  `area:diagnostics` to sweep VMs across all modules.
- **Cap the dispatcher queue / drop-old-on-burst policy.** Pin events are
  rare; the current FIFO behaviour is fine. Revisit if a future high-rate
  Arda event (movement, combat) needs throttling at the UI edge.
- **Mark `IMapPinState.Pins` as explicitly snapshot in the contract
  XML-doc.** Reasonable; can be a one-line edit folded into this work,
  but not load-bearing.

## Acceptance

1. `dotnet build Mithril.slnx` is 0 warnings / 0 errors.
2. `dotnet test Mithril.slnx` is all green (including new Arda.Wpf.Tests
   project).
3. **Determinism preserved.** `Arda.World.Player.Tests` passes unchanged;
   no `Arda.Wpf` reference added to any `Arda.*` project except `Arda.Wpf`
   itself (verify via `dotnet list reference`).
4. Manual verification (above) confirms the crash no longer reproduces and
   the pin list updates incrementally.
5. `WorldStateViewModel` no longer carries an `Action<Action> _dispatch`
   field; `DefaultDispatch` static is deleted.
6. `MapPins.Pins` / `PinsList` return a snapshot; a unit test pins this
   (so a future refactor can't quietly revert).
7. Issue filed before PR open; INDEX.md row updated from `active` to
   `shipped` post-merge.
