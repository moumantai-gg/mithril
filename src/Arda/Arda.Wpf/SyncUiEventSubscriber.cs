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
