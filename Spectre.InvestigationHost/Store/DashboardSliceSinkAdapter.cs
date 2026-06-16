using Spectre.DisparityFiltering;
using Spectre.DisparityFiltering.Sinks;

namespace Spectre.InvestigationHost.Store;

public sealed class DashboardSliceSinkAdapter : IDisparityGraphSliceSink
{
    private readonly IInvestigationStore _store;
    private int _disposed;

    public DashboardSliceSinkAdapter(IInvestigationStore store) => _store = store;

    public void Write(DisparityGraphSlice slice)
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
