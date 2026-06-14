namespace Spectre.CdmIngestion.Sinks;

/// <summary>
/// Receives graph facts immediately as they are projected.
/// </summary>
public interface IGraphFactSink : IDisposable
{
    /// <summary>
    /// Accepts one graph fact.
    /// </summary>
    /// <param name="fact">Fact to accept.</param>
    void Write(GraphFact fact);
}
