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
            () => UiEventSubscriber.SafeInvoke(handler, evt, _logger)));
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
