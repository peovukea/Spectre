namespace Spectre.InvestigationHost.Store;

public sealed class DashboardSliceSinkAdapter : IDisparityGraphSliceSink
{
    private readonly IInvestigationRunStore _store;
    private int _disposed;

    public DashboardSliceSinkAdapter(IInvestigationRunStore store) => _store = store;

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
