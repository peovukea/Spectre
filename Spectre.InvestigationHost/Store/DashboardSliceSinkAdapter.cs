using Spectre.SemanticIndexing;
using Spectre.SemanticIndexing.Sinks;

namespace Spectre.InvestigationHost.Store;

public sealed class DashboardSliceSinkAdapter : ISemanticGraphSliceSink
{
    private readonly DashboardQueryStore _store;
    private int _disposed;

    public DashboardSliceSinkAdapter(DashboardQueryStore store) => _store = store;

    public void Write(SemanticGraphSlice slice)
    {
        if (Volatile.Read(ref _disposed) != 0) return; // Silent reject after dispose
        _store.AcceptSlice(slice);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _store.MarkWritesClosed();
        }
    }
}
