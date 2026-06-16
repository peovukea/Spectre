using System.Threading.Channels;

namespace Spectre.InvestigationHost.Store;

public sealed record ServerSentEvent(string EventType, string Data)
{
    public long Id { get; init; }
}

public sealed class EventHub
{
    private readonly object _lock = new();
    private readonly HashSet<Channel<ServerSentEvent>> _subscribers = [];
    private long _nextEventId;

    public IAsyncEnumerable<ServerSentEvent> Subscribe(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<ServerSentEvent>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        lock (_lock)
        {
            _subscribers.Add(channel);
        }

        ct.Register(() =>
        {
            lock (_lock)
            {
                _subscribers.Remove(channel);
            }
        });

        return channel.Reader.ReadAllAsync(ct);
    }

    public void Publish(ServerSentEvent sse)
    {
        var eventWithId = sse with { Id = Interlocked.Increment(ref _nextEventId) };

        lock (_lock)
        {
            foreach (var channel in _subscribers)
            {
                channel.Writer.TryWrite(eventWithId);
            }
        }
    }
}
