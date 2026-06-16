namespace Spectre.CdmIngestion.Sinks;

/// <summary>
/// Forwards each graph fact to multiple child sinks in configured order.
/// </summary>
public sealed class CompositeGraphFactSink : IGraphFactFamilySink
{
    private readonly IReadOnlyList<IGraphFactSink> _sinks;
    private bool _disposed;

    /// <summary>
    /// Initializes a composite sink.
    /// </summary>
    /// <param name="sinks">Child sinks invoked in enumeration order.</param>
    public CompositeGraphFactSink(IEnumerable<IGraphFactSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks.ToArray();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Forwarding stops when a child throws. Child sinks that already accepted the fact are not rolled back.
    /// </remarks>
    public void Write(GraphFact fact)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var sink in _sinks)
        {
            sink.Write(fact);
        }
    }

    /// <inheritdoc />
    public void BeginFamily(string familyBasePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(familyBasePath);

        foreach (var sink in _sinks.OfType<IGraphFactFamilySink>())
        {
            sink.BeginFamily(familyBasePath);
        }
    }

    /// <inheritdoc />
    public void EndFamily(string familyBasePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(familyBasePath);

        foreach (var sink in _sinks.OfType<IGraphFactFamilySink>())
        {
            sink.EndFamily(familyBasePath);
        }
    }

    /// <inheritdoc />
    /// <remarks>Child sinks are disposed in reverse order.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception>? exceptions = null;

        for (var index = _sinks.Count - 1; index >= 0; index--)
        {
            try
            {
                _sinks[index].Dispose();
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        if (exceptions is { Count: 1 })
        {
            throw exceptions[0];
        }

        if (exceptions is { Count: > 1 })
        {
            throw new AggregateException("One or more graph-fact sinks failed during disposal.", exceptions);
        }
    }
}
