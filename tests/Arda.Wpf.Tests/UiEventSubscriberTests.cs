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
