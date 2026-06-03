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
        bus.Publish(new AreaChanged(PreviousArea: "AreaSerbule", CurrentArea: "AreaKur", Metadata: Meta()));

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
        public IReadOnlyList<MapPinEntry> Pins => _pins.ToArray();
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
