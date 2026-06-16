using Spectre.Indexing.Api.Sinks;
using Spectre.Ingestion.Api.Sinks;

namespace Spectre.Indexing.Api;

/// <summary>
/// Stage API for converting graph facts into semantic graph slices.
/// </summary>
public interface IIndexingStage
{
    /// <summary>
    /// Creates a graph-fact sink that owns the supplied downstream semantic slice sink.
    /// </summary>
    IGraphFactFamilySink CreateSink(ISemanticGraphSliceSink output, SemanticIndexingOptions? options = null);
}
