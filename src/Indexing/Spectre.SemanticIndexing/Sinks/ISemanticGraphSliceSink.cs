namespace Spectre.SemanticIndexing.Sinks;

/// <summary>Receives closed semantic graph slices.</summary>
public interface ISemanticGraphSliceSink : IDisposable
{
    /// <summary>Accepts one closed semantic graph slice.</summary>
    void Write(SemanticGraphSlice slice);
}
