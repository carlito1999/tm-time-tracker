using System.Threading.Channels;

namespace TmTimeTracker.Services;

public interface IEventBus
{
    ValueTask PublishAsync(DomainEvent evt, CancellationToken ct = default);
    IAsyncEnumerable<DomainEvent> Subscribe(CancellationToken ct = default);
}

public sealed class EventBus : IEventBus
{
    private readonly List<Channel<DomainEvent>> _subscribers = new();
    private readonly object _lock = new();

    public async ValueTask PublishAsync(DomainEvent evt, CancellationToken ct = default)
    {
        Channel<DomainEvent>[] snapshot;
        lock (_lock) snapshot = _subscribers.ToArray();
        foreach (var c in snapshot)
            await c.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DomainEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<DomainEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        lock (_lock) _subscribers.Add(channel);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return evt;
        }
        finally
        {
            lock (_lock) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
