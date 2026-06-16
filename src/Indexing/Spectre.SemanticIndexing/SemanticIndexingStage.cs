using Spectre.Indexing.Api.Sinks;
using Spectre.Ingestion.Api.Sinks;
using Spectre.SemanticIndexing.Sinks;

namespace Spectre.SemanticIndexing;

public sealed class SemanticIndexingStage : IIndexingStage
{
    public IGraphFactFamilySink CreateSink(ISemanticGraphSliceSink output, SemanticIndexingOptions? options = null) =>
        new SemanticIndexingGraphFactSink(output, options);
}
