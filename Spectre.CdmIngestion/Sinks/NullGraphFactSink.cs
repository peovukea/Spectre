namespace Spectre.CdmIngestion.Sinks;

/// <summary>
/// Accepts and discards all graph facts.
/// </summary>
public sealed class NullGraphFactSink : IGraphFactSink
{
    /// <inheritdoc />
    public void Write(GraphFact fact)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
