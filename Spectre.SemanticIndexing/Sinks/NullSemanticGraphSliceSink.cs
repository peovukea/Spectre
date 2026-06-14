namespace Spectre.SemanticIndexing.Sinks;

/// <summary>Accepts and discards semantic graph slices.</summary>
public sealed class NullSemanticGraphSliceSink : ISemanticGraphSliceSink
{
    /// <inheritdoc />
    public void Write(SemanticGraphSlice slice)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
